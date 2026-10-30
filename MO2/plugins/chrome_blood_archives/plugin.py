import hashlib
from pathlib import Path
from stat import IO_REPARSE_TAG_MOUNT_POINT, IO_REPARSE_TAG_SYMLINK

import mobase
from PyQt6.QtCore import QCoreApplication, QDir, QEvent, QEventLoop, QItemSelection, QItemSelectionModel, QModelIndex, QObject, QProcess, QTimer, Qt, qWarning
from PyQt6.QtGui import QAction, QIcon
from PyQt6.QtWidgets import QMainWindow, QMenu, QMessageBox, QTabWidget, QTreeView

from .bridge import ArchiveBridge
from .panel import ArchivesPanel
from .watcher import DirectoryWatcher, path_key


class WindowLifetime(QObject):
    def __init__(self, plugin):
        super().__init__(QCoreApplication.instance())
        self.plugin = plugin
        self.closing_window = None
        self.quitting = False
        self.exit_committed = False
        self.close_confirmed = False

    def eventFilter(self, watched, event):
        if watched is self.plugin.mod_view and event.type() == QEvent.Type.ChildAdded:
            QTimer.singleShot(0, self.plugin.bind_mod_selection)
        if event.type() not in (QEvent.Type.Close, QEvent.Type.Quit):
            return False
        plugin = self.plugin
        bridge = plugin.bridge
        if event.type() == QEvent.Type.Close and watched is not plugin.window:
            return False
        if plugin.active() and not self.exit_committed and not self.close_confirmed:
            if not plugin.confirm_close():
                event.ignore()
                return True
            self.close_confirmed = True
        if bridge is None or bridge.process.state() == QProcess.ProcessState.NotRunning:
            if plugin.active():
                if plugin.watcher is not None:
                    plugin.watcher.stop()
                plugin.detach_tab()
                QTimer.singleShot(0, self.restore_if_visible)
            return False
        self.quitting = event.type() == QEvent.Type.Quit
        self.closing_window = None if self.quitting else watched
        if plugin.watcher is not None:
            plugin.watcher.stop()
        if plugin.panel is not None:
            plugin.panel.preserve_draft()
            plugin.panel.status.setText("Finishing the archive operation before closing…")
            plugin.panel.shutdown()
        bridge.close()
        event.ignore()
        return True

    def drained(self):
        if self.exit_committed:
            return
        if self.quitting:
            self.quitting = False
            QTimer.singleShot(0, self.retry_quit)
        elif self.closing_window is not None:
            window = self.closing_window
            self.closing_window = None
            QTimer.singleShot(0, lambda: self.retry_close(window))

    def retry_quit(self):
        QCoreApplication.quit()
        QTimer.singleShot(0, self.restore_if_visible)

    def retry_close(self, window):
        window.close()
        QTimer.singleShot(0, self.restore_if_visible)

    def restore_if_visible(self):
        plugin = self.plugin
        if not self.exit_committed and plugin.window is not None and plugin.window.isVisible():
            self.close_confirmed = False
        if not self.exit_committed and plugin.window is not None and plugin.active() and plugin.window.isVisible() and plugin.panel is not None:
            was_closed = plugin.panel.closed
            if plugin.install_tab(scan_visible=False):
                if was_closed:
                    plugin.panel.invalidate(rescan=False)
                    plugin.panel.finish_operation()
                    plugin.panel.status.setText("Close cancelled. Refresh to read the current profile's archive order.")

    def about_to_quit(self):
        if self.exit_committed:
            return
        self.exit_committed = True
        plugin = self.plugin
        if plugin.watcher is not None:
            plugin.watcher.stop()
        if plugin.active():
            plugin.detach_tab()
        if plugin.panel is not None:
            plugin.panel.preserve_draft()
            plugin.panel.shutdown()
        bridge = plugin.bridge
        if bridge is not None:
            loop = QEventLoop()
            bridge.stopped.connect(loop.quit)
            bridge.close()
            if bridge.process.state() != QProcess.ProcessState.NotRunning:
                loop.exec(QEventLoop.ProcessEventsFlag.ExcludeUserInputEvents)
            bridge.stopped.disconnect(loop.quit)


class ChromeBloodArchives(mobase.IPluginTool, mobase.IPluginFileMapper):
    def __init__(self):
        mobase.IPluginTool.__init__(self)
        mobase.IPluginFileMapper.__init__(self)
        self.organizer = None
        self.window = None
        self.tabs = None
        self.panel = None
        self.bridge = None
        self.lifetime = None
        self.watcher = None
        self.watched_files = {}
        self.closed = False
        self.mod_view = None
        self.mod_selection = None
        self.syncing_selection = False
        self.archive_files = set()
        self.archive_directories = set()
        self.archive_ancestors = set()
        self.order_ancestors = set()
        self.watch_rebind = False
        self.pane_name = None
        self.native_pane_name = None
        self.tab_detached = False
        self.exit_action = None
        self.native_exit_action = None

    def name(self):
        return "Chrome & Blood Archives"

    def author(self):
        return "Chrome & Blood"

    def description(self):
        return "Orders archives per profile using Cyberpunk Conflict Studio."

    def version(self):
        return mobase.VersionInfo(1, 0, 0)

    def settings(self):
        return []

    def displayName(self):
        return "Archives Load Order"

    def tooltip(self):
        return "Open the active profile's archive order and resource conflicts."

    def icon(self):
        return QIcon()

    def init(self, organizer):
        self.organizer = organizer
        organizer.onUserInterfaceInitialized(self.ready)
        organizer.onPluginDisabled(self.name(), self.disabled)
        organizer.onPluginEnabled(self.name(), self.enabled)
        organizer.onProfileChanged(self.changed)
        organizer.onPluginSettingChanged(self.setting_changed)
        modlist = organizer.modList()
        modlist.onModStateChanged(self.mod_states_changed)
        modlist.onModMoved(self.changed)
        modlist.onModInstalled(self.changed)
        modlist.onModRemoved(self.changed)
        organizer.onAboutToRun(self.before_run)
        return True

    def active(self):
        if self.closed or self.organizer is None:
            return False
        game = self.organizer.managedGame()
        return game is not None and game.gameShortName() == "cyberpunk2077" and self.organizer.isPluginEnabled(self.name())

    def context(self):
        if not self.active():
            raise RuntimeError("Archives is disabled or this instance is not managing Cyberpunk 2077.")
        profile = self.organizer.profile()
        if profile is None:
            raise RuntimeError("Select a Mod Organizer profile, then Refresh Archives.")
        profile_path = Path(profile.absolutePath())
        if not profile_path.is_absolute():
            raise RuntimeError("Mod Organizer returned an invalid profile directory.")
        modlist = self.organizer.modList()
        providers = tuple((name, modlist.priority(name)) for name in reversed(modlist.allModsByProfilePriority()) if modlist.state(name) & mobase.ModState.ACTIVE)
        return (str(Path(self.organizer.basePath())), str(profile_path / "modlist.txt"), providers)

    def mappings(self):
        if not self.active() or self.write_blocked():
            return []
        profile = self.organizer.profile()
        if profile is None:
            return []
        directory = Path(profile.absolutePath())
        if not directory.is_absolute():
            return []
        source = directory / "archive-order.txt"
        if not source.is_file():
            source = directory / "archive-order" / "modlist.txt"
        if not source.is_file():
            return []
        destination = Path(self.organizer.managedGame().dataDirectory().absolutePath()) / "archive" / "pc" / "mod" / "modlist.txt"
        return [mobase.Mapping(str(source), str(destination), False, False)]

    def ready(self, window):
        if self.closed or self.window is not None or not isinstance(window, QMainWindow):
            return
        self.window = window
        self.mod_view = window.findChild(QTreeView, "modList")
        window.destroyed.connect(self.destroyed)
        self.lifetime = WindowLifetime(self)
        window.installEventFilter(self.lifetime)
        application = QCoreApplication.instance()
        if application is not None:
            application.installEventFilter(self.lifetime)
            application.aboutToQuit.connect(self.lifetime.about_to_quit)
        self.watcher = DirectoryWatcher(self.lifetime)
        self.watcher.changed.connect(self.files_changed)
        self.watcher.failed.connect(self.watch_failed)
        self.install_tab()

    def install_tab(self, scan_visible=True):
        if not self.active() or self.window is None:
            return False
        if self.organizer.appVersion().canonicalString() != "2.5.2.0":
            qWarning("Chrome & Blood Archives: this tab integration requires the bundled Mod Organizer 2.5.2.")
            return False
        tabs = self.window.findChild(QTabWidget, "tabWidget")
        if tabs is None or tabs.count() < 3:
            qWarning("Chrome & Blood Archives: the bundled Mod Organizer right-pane tabWidget is unavailable.")
            return False
        names = {tabs.widget(index).objectName().lower() for index in range(tabs.count())}
        if not {"datatab", "savestab", "downloadtab"}.issubset(names):
            qWarning("Chrome & Blood Archives: Mod Organizer's right-pane tab layout has changed.")
            return False
        self.tabs = tabs
        if self.bridge is None or self.bridge.closing:
            if self.bridge is not None and self.bridge.process.state() != QProcess.ProcessState.NotRunning:
                return False
            executable = Path(self.organizer.basePath()) / "Tools" / "Conflict Studio" / "Mo2Bridge" / "ConflictStudio.Mo2Bridge.exe"
            self.bridge = ArchiveBridge(executable, self.lifetime)
            self.bridge.result.connect(self.result)
            self.bridge.failed.connect(self.failed)
            self.bridge.progress.connect(self.progress)
            self.bridge.stopped.connect(self.stopped)
        if self.panel is None:
            self.native_pane_name = tabs.currentWidget().objectName()
            self.pane_name = self.organizer.persistent(self.name(), "selected_pane", self.native_pane_name)
            self.panel = ArchivesPanel(self, tabs)
            self.panel.setObjectName("chromeBloodArchivesTab")
            self.panel.archivesSelected.connect(self.select_mods)
            tabs.currentChanged.connect(self.tab_selected)
        self.panel.closed = False
        self.panel.setEnabled(True)
        self.bind_mod_selection()
        if tabs.indexOf(self.panel) < 0:
            self.tab_detached = True
            tabs.insertTab(0, self.panel, "Archives Load Order")
            for index in range(tabs.count()):
                if tabs.widget(index).objectName() == self.pane_name:
                    tabs.setCurrentIndex(index)
                    break
            self.tab_detached = False
        self.bind_exit_action()
        if self.panel.state is not None and not self.watcher.running():
            self.watch_state(self.panel.state)
        if scan_visible and tabs.currentWidget() is self.panel:
            self.panel.visible()
        return True

    def bind_exit_action(self):
        if self.exit_action is None:
            native = self.window.findChild(QAction, "actionExit")
            menu = self.window.findChild(QMenu, "menuFile")
            if native is None or menu is None or native not in menu.actions():
                return
            self.native_exit_action = native
            self.exit_action = QAction(native.icon(), native.text(), self.window)
            self.exit_action.setToolTip(native.toolTip())
            self.exit_action.setStatusTip(native.statusTip())
            self.exit_action.triggered.connect(self.window.close)
            menu.insertAction(native, self.exit_action)
        self.native_exit_action.setVisible(False)
        self.exit_action.setVisible(True)

    def detach_tab(self):
        if self.tabs is None or self.panel is None or self.tabs.indexOf(self.panel) < 0:
            return
        self.pane_name = self.tabs.currentWidget().objectName()
        self.organizer.setPersistent(self.name(), "selected_pane", self.pane_name)
        self.tab_detached = True
        self.tabs.removeTab(self.tabs.indexOf(self.panel))
        if self.pane_name == self.panel.objectName():
            for index in range(self.tabs.count()):
                if self.tabs.widget(index).objectName() == self.native_pane_name:
                    self.tabs.setCurrentIndex(index)
                    break

    def bind_mod_selection(self):
        if not self.active() or self.mod_view is None:
            return
        selection = self.mod_view.selectionModel()
        if selection is self.mod_selection:
            return
        self.unbind_mod_selection()
        self.mod_selection = selection
        if selection is not None:
            selection.selectionChanged.connect(self.select_archives)

    def unbind_mod_selection(self):
        if self.mod_selection is not None:
            try:
                self.mod_selection.selectionChanged.disconnect(self.select_archives)
            except RuntimeError:
                pass
            self.mod_selection = None

    def mod_indexes(self, parent=QModelIndex()):
        model = self.mod_view.model()
        for row in range(model.rowCount(parent)):
            index = model.index(row, 0, parent)
            yield index
            if model.hasChildren(index):
                yield from self.mod_indexes(index)

    def mod_name(self, index, names):
        identifier = index.data(Qt.ItemDataRole.UserRole + 1)
        return names[identifier] if type(identifier) is int and 0 <= identifier < len(names) else None

    def select_mods(self, archive_names):
        if self.syncing_selection or not self.active() or self.panel is None or self.panel.state is None or self.mod_view is None:
            return
        self.bind_mod_selection()
        if self.mod_selection is None:
            return
        selected = set(archive_names)
        providers = {row["provider"] for row in self.panel.archive_model.rows if row["name"] in selected}
        if not providers:
            return
        names = self.organizer.modList().allMods()
        indexes = [index for index in self.mod_indexes() if self.mod_name(index, names) in providers]
        if not indexes:
            if providers.intersection(names):
                self.panel.status.setText("Clear the mod-list filter to select this archive's mod.")
            return
        self.syncing_selection = True
        try:
            selection = QItemSelection()
            for index in indexes:
                parent = index.parent()
                while parent.isValid():
                    self.mod_view.expand(parent)
                    parent = parent.parent()
                selection.select(index, index)
            self.mod_selection.select(selection, QItemSelectionModel.SelectionFlag.ClearAndSelect | QItemSelectionModel.SelectionFlag.Rows)
            self.mod_selection.setCurrentIndex(indexes[0], QItemSelectionModel.SelectionFlag.NoUpdate)
            self.mod_view.scrollTo(indexes[0])
        finally:
            self.syncing_selection = False

    def select_archives(self, *_):
        if self.syncing_selection or not self.active() or self.panel is None or self.panel.closed or self.panel.state is None or self.mod_selection is None:
            return
        names = self.organizer.modList().allMods()
        providers = {self.mod_name(index, names) for index in self.mod_selection.selectedRows()}
        archives = [row["name"] for row in self.panel.archive_model.rows if row["provider"] in providers]
        self.syncing_selection = True
        try:
            self.panel.set_selected_archives(archives)
        finally:
            self.syncing_selection = False

    def display(self):
        if self.window is None:
            parent = self._parentWidget()
            if parent is not None and isinstance(parent.window(), QMainWindow):
                self.ready(parent.window())
        if not self.install_tab():
            QMessageBox.warning(self._parentWidget(), "Archives", "Could not attach Archives to this Mod Organizer window. Use the bundled Chrome & Blood Mod Organizer version and enable this plugin.")
            return
        self.tabs.setCurrentWidget(self.panel)
        self.panel.visible()

    def tab_selected(self, index):
        if self.tab_detached:
            return
        if self.tabs.widget(index) is not None and self.tabs.widget(index) is not self.panel:
            self.native_pane_name = self.tabs.widget(index).objectName()
        if self.panel is not None and self.tabs.widget(index) is self.panel:
            self.panel.visible()

    def confirm_close(self):
        if self.panel is None:
            return True
        self.panel.preserve_draft()
        if not self.panel.drafts:
            return True
        dialog = QMessageBox(self.window)
        dialog.setWindowTitle("Unsaved archive orders")
        dialog.setText("Archive orders have unsaved changes. Closing Mod Organizer will discard these drafts.")
        profiles = sorted(Path(path).parent.name for path in self.panel.drafts)
        dialog.setInformativeText("Profiles: " + ", ".join(profiles))
        close = dialog.addButton("Close without saving", QMessageBox.ButtonRole.DestructiveRole)
        keep = dialog.addButton("Keep MO2 open", QMessageBox.ButtonRole.RejectRole)
        dialog.setDefaultButton(keep)
        dialog.setEscapeButton(keep)
        dialog.exec()
        return dialog.clickedButton() is close

    def changed(self, *_):
        if self.panel is not None:
            self.panel.invalidate()

    def mod_states_changed(self, states):
        self.changed()

    def confirm_draft(self):
        dialog = QMessageBox(self.window)
        dialog.setWindowTitle("Saved archive order changed")
        dialog.setText("The saved order changed while you were editing. Which order should this profile use?")
        keep = dialog.addButton("Save my draft", QMessageBox.ButtonRole.AcceptRole)
        saved = dialog.addButton("Use saved order", QMessageBox.ButtonRole.DestructiveRole)
        dialog.addButton("Cancel", QMessageBox.ButtonRole.RejectRole)
        dialog.exec()
        return "draft" if dialog.clickedButton() is keep else "saved" if dialog.clickedButton() is saved else "cancel"

    def before_run(self, app_path: str, cwd: QDir, arguments: str) -> bool:
        if not self.active() or self.panel is None:
            return True
        game = self.organizer.managedGame()
        game_binary = Path(game.gameDirectory().absoluteFilePath(game.binaryName()))
        if Path(app_path) != game_binary:
            return True
        pending = self.bridge.pending if self.bridge is not None else None
        if pending is not None and pending["operation"] in ("apply", "undo"):
            QMessageBox.information(self.window, "Archive order is being saved", "Wait for the archive order to finish saving, then run the game again.")
            return False
        profile = self.organizer.profile()
        path = str(Path(profile.absolutePath()) / "modlist.txt") if profile is not None else ""
        dirty = self.panel.state is not None and self.panel.state["profilePath"] == path and self.panel.has_draft() or path in self.panel.drafts
        if not dirty:
            return True
        dialog = QMessageBox(self.window)
        dialog.setWindowTitle("Unsaved archive order")
        dialog.setText("This profile has an unsaved archive order. The game uses the last saved order.")
        save = dialog.addButton("Return to save", QMessageBox.ButtonRole.AcceptRole)
        launch = dialog.addButton("Launch saved order", QMessageBox.ButtonRole.ActionRole)
        dialog.addButton("Cancel", QMessageBox.ButtonRole.RejectRole)
        dialog.exec()
        if dialog.clickedButton() is save:
            self.display()
        return dialog.clickedButton() is launch

    def setting_changed(self, plugin_name, setting, *_):
        game = self.organizer.managedGame()
        if game is not None and plugin_name == game.name() and setting == "enforce_archive_load_order":
            self.changed()

    def result(self, identifier, operation, data):
        if self.panel is not None:
            if operation in ("scan", "apply", "undo") and self.write_blocked():
                data["writeBlockedReason"] = self.write_blocked()
            self.panel.result(identifier, operation, data)
            if operation == "scan" and not self.panel.selection_names:
                self.select_archives()

    def write_blocked(self):
        game = self.organizer.managedGame() if self.organizer is not None else None
        if game is not None and self.organizer.pluginSetting(game.name(), "enforce_archive_load_order"):
            return "Cyberpunk's support plugin is generating archive order. Disable enforce_archive_load_order in its plugin settings, then Refresh Archives."
        return None

    def failed(self, identifier, operation, message, cancelled):
        if self.panel is not None:
            self.panel.failed(identifier, operation, message, cancelled)

    def progress(self, identifier, data):
        if self.panel is not None:
            self.panel.progress(identifier, data)

    def refresh_mappings(self):
        self.organizer.refresh(False)

    def watch_state(self, state):
        if self.watcher is None or not self.active():
            return
        try:
            self.configure_watches(state)
        except OSError as error:
            self.watcher.stop()
            self.watch_failed(str(error))

    def configure_watches(self, state):
        files = {state["profilePath"], state["writeTarget"]}
        if state.get("orderSource"):
            files.add(state["orderSource"])
        archives = {row["physicalPath"] for row in state.get("archiveRows", [])}
        directories = set(state.get("archiveDirectories", []))
        self.watched_files = {}
        self.archive_files = set()
        self.archive_directories = set()
        self.archive_ancestors = set()
        self.order_ancestors = set()
        profile = self.organizer.profile()
        game = self.organizer.managedGame()
        configured = {Path(self.organizer.modsPath()), Path(self.organizer.overwritePath()), Path(profile.absolutePath()).parent, Path(game.gameDirectory().absolutePath())}
        root_components = configured | {parent for root in configured for parent in root.parents}
        paths = {name: Path(name) for name in files | archives | directories}
        links = {}
        canonical = {}
        for path in set(paths.values()) | configured:
            components = (path, *path.parents)
            for component in components:
                if component not in links:
                    try:
                        tag = component.lstat().st_reparse_tag
                    except (FileNotFoundError, NotADirectoryError):
                        tag = 0
                    links[component] = component.resolve() if tag in (IO_REPARSE_TAG_MOUNT_POINT, IO_REPARSE_TAG_SYMLINK) else None
            linked = next((component for component in components if links[component] is not None), None)
            canonical[path] = links[linked] / path.relative_to(linked) if linked is not None else path
        roots = {canonical[path] for path in configured}
        roots.update(target.parent for component, target in links.items() if target is not None and component not in root_components)
        for name, path in paths.items():
            variants = {path_key(path), path_key(canonical[path])}
            if name in files:
                signature = self.file_signature(name)
                self.watched_files.update({variant: (name, signature) for variant in variants})
            if name in archives:
                self.archive_files.update(variants)
            if name in directories:
                self.archive_directories.update(variants)
            for variant in variants:
                parents = {str(parent) for parent in Path(variant).parents}
                if name in files:
                    self.order_ancestors.update(parents)
                if name in archives or name in directories:
                    self.archive_ancestors.update(parents)
                    if name in directories:
                        self.archive_ancestors.add(variant)
        existing = set()
        for root in roots:
            while not root.is_dir() and root != root.parent:
                root = root.parent
            existing.add(root)
        recursive = {root for root in existing if not any(parent in existing for parent in root.parents)}
        guarded = {root.parent for root in recursive if root != root.parent and not any(parent in recursive for parent in (root.parent, *root.parent.parents))}
        watches = {(path_key(root), True) for root in recursive} | {(path_key(root), False) for root in guarded}
        rebind = self.watch_rebind
        self.watch_rebind = False
        self.watcher.configure(watches, set(self.watched_files) | self.archive_files, self.archive_directories, self.archive_ancestors | self.order_ancestors, force=rebind)

    def watch_failed(self, message):
        qWarning("Chrome & Blood Archives: archive monitoring stopped: " + message)
        if self.panel is not None and not self.panel.closed:
            self.panel.invalidate(rescan=False)
            self.panel.status.setText("Archive monitoring stopped. Refresh to read the current files. " + message)

    def file_signature(self, name):
        path = Path(name)
        try:
            limit = 1024 * 1024
            metadata = path.stat()
            if metadata.st_size > limit:
                return (metadata.st_size, metadata.st_mtime_ns)
            with path.open("rb") as stream:
                data = stream.read(limit + 1)
            if len(data) > limit:
                metadata = path.stat()
                return (metadata.st_size, metadata.st_mtime_ns)
            return hashlib.sha256(data).digest()
        except OSError:
            return None

    def files_changed(self, changes):
        for name, action in changes:
            if self.file_changed(name, action):
                break

    def file_changed(self, name=None, action=3):
        if self.bridge is not None and self.bridge.pending is not None and self.bridge.pending["operation"] in ("apply", "undo"):
            return False
        if name is not None:
            name = path_key(name)
        archive_changed = name in self.archive_files or name is not None and Path(name).suffix.casefold() == ".archive" and str(Path(name).parent) in self.archive_directories
        directory_changed = action != 3 and (name in self.archive_ancestors or name in self.order_ancestors)
        if archive_changed or directory_changed:
            self.watch_rebind = self.watch_rebind or directory_changed
            self.changed()
            return True
        watched = self.watched_files.values() if name is None else (self.watched_files[name],) if name in self.watched_files else ()
        if any(self.file_signature(path) != signature for path, signature in watched):
            self.changed()
            return True
        return False

    def disabled(self):
        self.unbind_mod_selection()
        self.detach_tab()
        if self.exit_action is not None:
            self.exit_action.setVisible(False)
            self.native_exit_action.setVisible(True)
        if self.panel is not None:
            self.panel.invalidate()
            self.panel.shutdown()
        if self.bridge is not None:
            self.bridge.close()
        if self.watcher is not None:
            self.watcher.stop()
        self.watched_files = {}
        self.archive_files = set()
        self.archive_directories = set()
        self.archive_ancestors = set()
        self.order_ancestors = set()
        self.watch_rebind = False

    def enabled(self):
        if not self.closed and self.window is not None:
            QTimer.singleShot(0, self.install_tab)

    def stopped(self):
        shutting_down = self.lifetime is not None and (self.lifetime.quitting or self.lifetime.closing_window is not None)
        if self.lifetime is not None:
            self.lifetime.drained()
        if not shutting_down and self.active() and self.panel is not None and self.bridge.closing:
            QTimer.singleShot(0, self.install_tab)
        if self.closed and self.lifetime is not None:
            self.lifetime.deleteLater()

    def destroyed(self, *_):
        self.closed = True
        if self.watcher is not None:
            self.watcher.stop()
        application = QCoreApplication.instance()
        if application is not None and self.lifetime is not None:
            application.removeEventFilter(self.lifetime)
            application.aboutToQuit.disconnect(self.lifetime.about_to_quit)
        if self.panel is not None:
            self.panel.shutdown()
        if self.bridge is not None:
            self.bridge.close()
        self.window = None
        self.panel = None
        self.tabs = None
        self.mod_view = None
        self.mod_selection = None
