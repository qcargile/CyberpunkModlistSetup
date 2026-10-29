import os
import struct
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "plugins"))

try:
    import mobase
    from PyQt6.QtCore import QCoreApplication, QDir, QEvent, QEventLoop, QModelIndex, QProcess, Qt, QTimer
    from PyQt6.QtGui import QCloseEvent
    from PyQt6.QtWidgets import QApplication, QMainWindow, QStyleOptionViewItem, QTableView, QTabWidget, QTreeView, QWidget
    from chrome_blood_archives import createPlugin
    from chrome_blood_archives.bridge import ArchiveBridge
    from chrome_blood_archives.models import ArchiveModel
    from chrome_blood_archives.views import ArchiveList, ArchiveScrollBar
except ImportError:
    mobase = None


class Organizer:
    def __init__(self, root):
        self.root = Path(root)
        self.profile_path = self.root / "profiles" / "First"
        self.profile_path.mkdir(parents=True)
        (self.profile_path / "modlist.txt").write_text("+High\n+Low\n", encoding="utf-8")
        package = self.root / "plugins" / "chrome_blood_archives"
        package.mkdir(parents=True)
        (package / "__init__.py").write_text("", encoding="utf-8")
        self.game = None
        self.enabled = True
        self.generator = False
        self.callbacks = {}
        self.names = ["Low", "Disabled", "High"]
        self.refreshes = []
        self.persisted = {}

    def persistent(self, name, key, default=None):
        return self.persisted.get((name, key), default)

    def setPersistent(self, name, key, value):
        self.persisted[name, key] = value

    def basePath(self):
        return str(self.root)

    def profile(self):
        return SimpleNamespace(absolutePath=lambda: str(self.profile_path))

    def managedGame(self):
        return self.game

    def activate_game(self, short_name="cyberpunk2077"):
        self.game = SimpleNamespace(gameShortName=lambda: short_name, name=lambda: "Cyberpunk 2077 Support Plugin", dataDirectory=lambda: QDir(str(self.root / "Game")), gameDirectory=lambda: QDir(str(self.root / "Game")), binaryName=lambda: "bin/x64/Cyberpunk2077.exe")

    def isPluginEnabled(self, name):
        return self.enabled

    def pluginSetting(self, name, key):
        return self.generator

    def appVersion(self):
        return mobase.VersionInfo(2, 5, 2)

    def modList(self):
        return self

    def allModsByProfilePriority(self):
        return self.names

    def priority(self, name):
        return self.names.index(name)

    def state(self, name):
        return mobase.ModState.EXISTS if name == "Disabled" else mobase.ModState.ACTIVE

    def refresh(self, save_changes=True):
        self.refreshes.append(save_changes)

    def onUserInterfaceInitialized(self, callback):
        self.callbacks["ready"] = callback

    def onPluginEnabled(self, name, callback):
        self.callbacks["enabled"] = callback

    def onPluginDisabled(self, name, callback):
        self.callbacks["disabled"] = callback

    def onProfileChanged(self, callback):
        self.callbacks["profile"] = callback

    def onPluginSettingChanged(self, callback):
        self.callbacks["setting"] = callback

    def onModStateChanged(self, callback):
        self.callbacks["state"] = callback

    def onModMoved(self, callback):
        self.callbacks["moved"] = callback

    def onModInstalled(self, callback):
        self.callbacks["installed"] = callback

    def onModRemoved(self, callback):
        self.callbacks["removed"] = callback

    def onAboutToRun(self, callback):
        self.callbacks["run"] = callback


def archive(name, editable=True):
    return {"name": name, "provider": "Mod " + name, "physicalPath": "C:/mods/" + name, "position": 0, "editable": editable, "winning": 2, "losing": 1, "same": 0, "unknown": 0, "unique": 4}


def observe(signal):
    events = []
    signal.connect(lambda *arguments: events.append(list(arguments)))
    return events


@unittest.skipIf(mobase is None, "Requires MO2's native Python 3.12 and Qt libraries")
class ArchivesQtTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.application = QApplication.instance() or QApplication([])
        cls.application.setQuitOnLastWindowClosed(False)

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.organizer = Organizer(self.directory.name)
        self.plugin = createPlugin()
        self.assertTrue(self.plugin.init(self.organizer))
        self.slot_errors = []
        original = sys.excepthook
        sys.excepthook = lambda kind, error, trace: self.slot_errors.append((kind, error))
        self.addCleanup(setattr, sys, "excepthook", original)

    def tearDown(self):
        if self.plugin.panel is not None:
            self.plugin.panel.shutdown()
        if self.plugin.bridge is not None:
            self.plugin.bridge.close()
        if self.plugin.window is not None:
            self.plugin.window.deleteLater()
        QCoreApplication.sendPostedEvents(None, QEvent.Type.DeferredDelete)
        self.application.processEvents()
        self.assertEqual(self.slot_errors, [])

    def window(self, window=None):
        window = window or QMainWindow()
        tabs = QTabWidget(window)
        tabs.setObjectName("tabWidget")
        for name in ("dataTab", "savesTab", "downloadTab"):
            page = QWidget(tabs)
            page.setObjectName(name)
            tabs.addTab(page, name)
        window.setCentralWidget(tabs)
        self.organizer.activate_game()
        self.organizer.callbacks["ready"](window)
        return window, tabs

    def state(self):
        return {"token": "scan-one", "profilePath": str(self.organizer.profile_path / "modlist.txt"), "profileName": "First", "orderSource": None, "writeTarget": str(self.organizer.profile_path / "archive-order" / "modlist.txt"), "writeBlockedReason": None, "effectiveOrder": ["a.archive", "b.archive", "REDmod/c.archive"], "editableOrder": ["a.archive", "b.archive"], "archiveRows": [archive("a.archive"), archive("b.archive"), archive("REDmod/c.archive", False)], "canUndo": False, "archiveFailures": [], "archiveWarnings": [], "orderMessage": "", "palette": [{"tone": "Wins", "colorHex": "#42E58B", "backgroundHex": "#08251B"}]}

    def loaded(self):
        self.window()
        panel = self.plugin.panel
        panel.request_context = self.plugin.context()
        panel.operation_generation = panel.generation
        panel.needs_scan = False
        panel.result("one", "scan", self.state())
        panel.timer.stop()
        return panel

    def test_single_plugin_exposes_both_native_interfaces(self):
        self.assertIsInstance(self.plugin, mobase.IPluginTool)
        self.assertIsInstance(self.plugin, mobase.IPluginFileMapper)
        self.assertIn("ready", self.organizer.callbacks)
        self.assertEqual(self.plugin.mappings(), [])

    def test_late_managed_game_attaches_once_and_remains_lazy(self):
        window, tabs = self.window()
        self.assertEqual(tabs.count(), 4)
        self.assertEqual(tabs.tabText(0), "Archives Load Order")
        self.plugin.ready(window)
        self.plugin.install_tab()
        self.assertEqual(tabs.count(), 4)
        self.assertIsNone(self.plugin.bridge.pending)
        self.assertEqual(self.plugin.bridge.process.state(), QProcess.ProcessState.NotRunning)

    def test_unsupported_right_pane_is_not_modified(self):
        self.organizer.activate_game()
        window = QMainWindow()
        tabs = QTabWidget(window)
        tabs.setObjectName("tabWidget")
        for name in ("one", "two", "three"):
            tabs.addTab(QWidget(), name)
        window.setCentralWidget(tabs)
        self.plugin.ready(window)
        self.assertIsNone(self.plugin.panel)
        self.assertEqual(tabs.count(), 3)

    def test_mapping_is_native_current_profile_only_and_absent_until_save(self):
        self.organizer.activate_game()
        self.assertEqual(self.plugin.mappings(), [])
        target = self.organizer.profile_path / "archive-order" / "modlist.txt"
        target.parent.mkdir()
        target.write_text("b.archive\na.archive\n", encoding="utf-8")
        mapping, = self.plugin.mappings()
        self.assertIsInstance(mapping, mobase.Mapping)
        self.assertEqual(Path(mapping.source), target)
        self.assertEqual(Path(mapping.destination), self.organizer.root / "Game" / "archive" / "pc" / "mod" / "modlist.txt")
        self.assertFalse(mapping.isDirectory)
        self.assertFalse(mapping.createTarget)
        self.organizer.profile_path = self.organizer.root / "profiles" / "Other"
        self.assertEqual(self.plugin.mappings(), [])
        self.assertEqual(target.read_text(encoding="utf-8"), "b.archive\na.archive\n")

    def test_disabled_other_game_and_competing_generator_have_no_mapping(self):
        self.organizer.activate_game()
        target = self.organizer.profile_path / "archive-order" / "modlist.txt"
        target.parent.mkdir()
        target.write_text("a.archive", encoding="utf-8")
        self.organizer.generator = True
        self.assertEqual(self.plugin.mappings(), [])
        self.assertIn("enforce_archive_load_order", self.plugin.write_blocked())
        self.organizer.generator = False
        self.organizer.enabled = False
        self.assertEqual(self.plugin.mappings(), [])
        self.organizer.enabled = True
        self.organizer.activate_game("other")
        self.assertEqual(self.plugin.mappings(), [])

    def test_compiler_safe_profile_file_takes_precedence_over_legacy_order(self):
        self.organizer.activate_game()
        legacy = self.organizer.profile_path / "archive-order" / "modlist.txt"
        legacy.parent.mkdir()
        legacy.write_text("b.archive\na.archive\n", encoding="utf-8")
        current = self.organizer.profile_path / "archive-order.txt"
        current.write_text("a.archive\nb.archive\n", encoding="utf-8")
        mapping, = self.plugin.mappings()
        self.assertEqual(Path(mapping.source), current)
        self.assertEqual(Path(mapping.destination).name, "modlist.txt")
        self.assertEqual(legacy.read_text(encoding="utf-8"), "b.archive\na.archive\n")

    def test_context_uses_live_active_providers_in_descending_priority(self):
        self.organizer.activate_game()
        root, path, providers = self.plugin.context()
        self.assertEqual(providers, (("High", 2), ("Low", 0)))
        self.assertEqual(Path(path), self.organizer.profile_path / "modlist.txt")

    def test_drag_moves_archives_and_emits_order_without_writing(self):
        model = ArchiveModel()
        model.reset([archive("a"), archive("b"), archive("c"), archive("REDmod/d", False)], ["a", "b", "c"], "one")
        model.writable = True
        changed = observe(model.orderChanged)
        mime = model.mimeData([model.index(0, 0)])
        self.assertTrue(model.dropMimeData(mime, Qt.DropAction.MoveAction, 3, 0, QModelIndex()))
        self.assertEqual(model.editable_order, ["b", "c", "a"])
        self.assertEqual([row["name"] for row in model.rows], ["b", "c", "a", "REDmod/d"])
        self.assertEqual(len(changed), 1)
        self.assertFalse(model.dropMimeData(mime, Qt.DropAction.MoveAction, 4, 0, QModelIndex()))
        self.assertFalse(model.flags(model.index(3, 0)) & Qt.ItemFlag.ItemIsDragEnabled)
        model.session = "next-profile"
        self.assertFalse(model.dropMimeData(mime, Qt.DropAction.MoveAction, 0, 0, QModelIndex()))

    def test_colors_and_text_are_exact_backend_relationship_values(self):
        model = ArchiveModel()
        model.reset([archive("a"), archive("b")], ["a", "b"], "one")
        model.set_relationships([{"archiveName": "b", "tone": "Wins", "colorHex": "#42E58B", "backgroundHex": "#08251B"}])
        self.assertEqual(model.data(model.index(1, 0), Qt.ItemDataRole.BackgroundRole).color().name(), "#08251b")
        self.assertEqual(model.data(model.index(1, 0), Qt.ItemDataRole.ForegroundRole).color().name(), "#42e58b")
        self.assertEqual(model.data(model.index(1, 7), Qt.ItemDataRole.AccessibleTextRole), "Wins")
        self.assertIsNone(model.data(model.index(0, 0), Qt.ItemDataRole.BackgroundRole))

    def test_profile_change_discards_state_and_ignores_stale_result(self):
        panel = self.loaded()
        panel.archive_model.writable = True
        panel.invalidate()
        panel.result("old", "scan", self.state())
        self.assertIsNone(panel.state)
        self.assertEqual(panel.archive_model.rowCount(), 0)
        self.assertFalse(panel.save_button.isEnabled())
        self.assertFalse(panel.undo_button.isEnabled())

    def test_preview_is_required_and_unknown_acknowledgement_rechecks(self):
        panel = self.loaded()
        model = panel.archive_model
        mime = model.mimeData([model.index(0, 0)])
        self.assertTrue(model.dropMimeData(mime, Qt.DropAction.MoveAction, 2, 0, QModelIndex()))
        self.assertFalse(panel.save_button.isEnabled())
        panel.pending_preview = False
        preview = {"changedArchives": ["a.archive", "b.archive"], "winnerDeltas": [], "totalWinnerDeltas": 0, "winnerChangesKnown": False, "truncated": False, "canApply": False, "blockedReason": "Acknowledge unknown effects.", "unknownImpact": "RequiresAcknowledgement"}
        panel.result("preview", "preview", preview)
        self.assertFalse(panel.save_button.isEnabled())
        self.assertFalse(panel.acknowledge.isHidden())
        panel.acknowledge.setChecked(True)
        self.assertIsNone(panel.preview)
        self.assertTrue(panel.pending_preview)
        panel.pending_preview = False
        panel.result("preview2", "preview", {**preview, "canApply": True, "blockedReason": None})
        self.assertTrue(panel.save_button.isEnabled())
        self.assertEqual(panel.state["editableOrder"], ["a.archive", "b.archive"])

    def test_unknown_winner_changes_never_claim_zero_and_keep_core_save_decision(self):
        panel = self.loaded()
        model = panel.archive_model
        mime = model.mimeData([model.index(0, 0)])
        self.assertTrue(model.dropMimeData(mime, Qt.DropAction.MoveAction, 2, 0, QModelIndex()))
        panel.pending_preview = False
        preview = {"changedArchives": ["a.archive", "b.archive"], "winnerDeltas": [], "totalWinnerDeltas": 0, "winnerChangesKnown": True, "truncated": False, "canApply": True, "blockedReason": None, "unknownImpact": "None"}
        panel.result("known", "preview", preview)
        self.assertIn("0 resource winners change", panel.preview_label.text())
        panel.result("unknown", "preview", {**preview, "winnerChangesKnown": False})
        self.assertIn("cannot be fully determined", panel.preview_label.text())
        self.assertNotIn("0 resource winners change", panel.preview_label.text())
        self.assertFalse(hasattr(panel, "details_button"))
        self.assertTrue(panel.save_button.isEnabled())

    def test_minimal_list_search_selects_without_filtering_or_details(self):
        panel = self.loaded()
        self.assertIsInstance(panel.archives, QTreeView)
        self.assertFalse(panel.archives.isColumnHidden(1))
        self.assertTrue(panel.archives.uniformRowHeights())
        self.assertEqual(panel.findChildren(QTableView), [])
        self.assertTrue(panel.preview_label.isHidden())
        events = observe(panel.archivesSelected)
        panel.search.setText("B.ARCHIVE")
        self.assertEqual(panel.selection_names, ["b.archive"])
        self.assertEqual(events[-1], [["b.archive"]])
        self.assertEqual(panel.archive_model.rowCount(), 3)
        self.assertEqual(panel.archive_model.editable_order, ["a.archive", "b.archive"])
        self.assertNotIn("Order:", panel.status.text())
        self.assertFalse(hasattr(panel, "resources"))
        self.assertFalse(hasattr(panel, "legend"))

    def test_list_drag_preserves_selected_archive_and_order_signals(self):
        panel = self.loaded()
        panel.set_selected_archives(["a.archive"])
        model = panel.archive_model
        mime = model.mimeData(panel.archives.selectionModel().selectedIndexes())
        self.assertTrue(model.dropMimeData(mime, Qt.DropAction.MoveAction, 2, 0, QModelIndex()))
        self.assertEqual(model.editable_order, ["b.archive", "a.archive"])
        self.assertEqual(panel.selection_names, ["a.archive"])
        self.assertEqual(panel.archives.selectionModel().selectedRows(1)[0].column(), 1)
        self.assertTrue(panel.pending_preview)
        self.assertFalse(panel.preview_label.isHidden())

    def test_native_tree_delegate_and_scrollbar_remain_compact(self):
        panel = self.loaded()
        model = panel.archive_model
        option = QStyleOptionViewItem()
        option.font = panel.archives.font()
        option.fontMetrics = panel.archives.fontMetrics()
        option.palette = panel.archives.palette()
        self.assertGreaterEqual(panel.archives.itemDelegate().sizeHint(option, model.index(1, 1)).height(), 22)
        self.assertIsInstance(panel.archives.verticalScrollBar(), ArchiveScrollBar)

    def test_native_scrollbar_paints_relationship_and_selection_markers(self):
        model = ArchiveModel()
        names = [f"{number}.archive" for number in range(120)]
        model.reset([archive(name) for name in names], names, "markers")
        view = ArchiveList(model)
        view.setAttribute(Qt.WidgetAttribute.WA_DontShowOnScreen)
        view.resize(240, 180)
        view.show()
        view.setCurrentIndex(model.index(0, 1))
        model.set_relationships([{"archiveName": "40.archive", "tone": "Wins", "colorHex": "#42E58B", "backgroundHex": "#08251B"}])
        self.application.processEvents()
        scrollbar = view.verticalScrollBar()
        image = scrollbar.grab().toImage()
        colors = {image.pixelColor(x, y).name() for x in range(image.width()) for y in range(image.height())}
        self.assertIn("#42e58b", colors)
        self.assertIn(view.palette().highlight().color().name(), colors)
        self.assertEqual(scrollbar.orientation(), Qt.Orientation.Vertical)
        view.deleteLater()

    def test_external_order_watch_invalidates_but_unchanged_content_does_not(self):
        panel = self.loaded()
        self.plugin.file_changed()
        self.assertIsNotNone(panel.state)
        path = Path(panel.state["profilePath"])
        path.write_text("+Changed\n", encoding="utf-8")
        self.plugin.file_changed()
        self.assertIsNone(panel.state)

    def test_hidden_selection_waits_until_the_archives_tab_is_visible(self):
        panel = self.loaded()
        window = panel.window()
        window.setAttribute(Qt.WidgetAttribute.WA_DontShowOnScreen)
        window.show()
        panel.set_selected_archives(["b.archive"])
        panel.timer.stop()
        with patch.object(self.plugin.bridge, "request") as request:
            panel.dispatch()
            request.assert_not_called()
            self.assertTrue(panel.pending_selection)
            self.plugin.tabs.setCurrentWidget(panel)
            self.application.processEvents()
            request.assert_called_once()
            self.assertEqual(request.call_args.args, ("select",))
            self.assertEqual(request.call_args.kwargs["selectedArchives"], ["b.archive"])

    def test_a_replaced_selection_does_not_display_the_previous_relationships(self):
        panel = self.loaded()
        panel.set_selected_archives(["a.archive"])
        panel.pending_selection = False
        panel.set_selected_archives(["b.archive"])
        relationships = [{"archiveName": "b.archive", "tone": "Wins", "colorHex": "#42E58B", "backgroundHex": "#08251B"}]

        panel.result("previous", "select", {"relationships": relationships})

        self.assertEqual(panel.archive_model.relationships, {})
        self.assertTrue(panel.pending_selection)
        panel.pending_selection = False
        panel.result("current", "select", {"relationships": relationships})
        self.assertEqual(panel.archive_model.relationships["b.archive"]["tone"], "Wins")

    def test_disable_removes_tab_and_reenable_restores_lazy_tab(self):
        self.window()
        self.organizer.enabled = False
        self.plugin.disabled()
        self.assertEqual(self.plugin.tabs.indexOf(self.plugin.panel), -1)
        self.assertTrue(self.plugin.panel.closed)
        self.organizer.enabled = True
        self.plugin.enabled()
        self.application.processEvents()
        self.assertGreaterEqual(self.plugin.tabs.indexOf(self.plugin.panel), 0)
        self.assertFalse(self.plugin.panel.closed)
        self.assertIsNone(self.plugin.bridge.pending)

    def test_bridge_partial_json_progress_stale_ids_and_error(self):
        bridge = ArchiveBridge("missing")
        bridge.pending = {"id": "one", "operation": "scan"}
        progress = observe(bridge.progress)
        result = observe(bridge.result)
        bridge.feed(b'{"id":"stale","type":"result","ok":true,"data":{}}\n')
        bridge.feed(b'{"id":"one","type":"progress","phase":"hash","completed":1,"total":2}\n')
        bridge.feed(b'{"id":"one","type":"result","ok":true,"data":')
        self.assertEqual(len(result), 0)
        bridge.feed(b'{"token":"current"}}\n')
        self.assertEqual(len(progress), 1)
        self.assertEqual(result[0], ["one", "scan", {"token": "current"}])
        bridge.pending = {"id": "two", "operation": "apply"}
        failed = observe(bridge.failed)
        bridge.feed(b'{"id":"two","type":"error","ok":false,"message":"Inventory changed","cancelled":false}\n')
        self.assertEqual(failed[0], ["two", "apply", "Inventory changed", False])
        self.assertIsNone(bridge.pending)

    def test_bridge_bad_json_and_response_limit_fail_explicitly(self):
        bridge = ArchiveBridge("missing")
        bridge.pending = {"id": "one", "operation": "scan"}
        failed = observe(bridge.failed)
        bridge.feed(b"invalid\n")
        self.assertEqual(len(failed), 1)
        self.assertTrue(bridge.closing)
        other = ArchiveBridge("missing")
        other.pending = {"id": "two", "operation": "scan"}
        failed2 = observe(other.failed)
        with patch("chrome_blood_archives.bridge.MAX_LINE_BYTES", 10):
            other.feed(b"x" * 11)
        self.assertEqual(len(failed2), 1)
        self.assertEqual(other.buffer, b"")

    def test_bridge_native_process_failed_start_is_actionable(self):
        bridge = ArchiveBridge(str(Path(self.directory.name) / "not-installed.exe"))
        failed = observe(bridge.failed)
        loop = QEventLoop()
        bridge.failed.connect(lambda *_: loop.quit())
        bridge.request("scan", instanceRoot=self.directory.name)
        QTimer.singleShot(2000, loop.quit)
        loop.exec()
        self.assertEqual(len(failed), 1)
        self.assertIn("Restore Tools/Conflict Studio/Mo2Bridge", failed[0][2])
        self.assertIsNone(bridge.pending)

    def test_cancel_while_starting_is_sent_after_the_original_request(self):
        bridge = ArchiveBridge("missing")
        bridge.pending = {"id": "one", "operation": "scan"}
        with patch.object(bridge.process, "state", return_value=QProcess.ProcessState.Starting), patch.object(bridge, "_write") as write:
            bridge.cancel()
            self.assertTrue(bridge.cancel_requested)
            write.assert_not_called()
            bridge._started()
            self.assertEqual(write.call_args_list[0].args[0], bridge.pending)
            self.assertEqual(write.call_args_list[1].args[0]["operation"], "cancel")

    def test_actual_engine_process_scan_select_preview_save_undo_and_eof(self):
        executable = os.environ.get("MO2_ARCHIVE_BRIDGE_EXE")
        if not executable:
            self.skipTest("Set MO2_ARCHIVE_BRIDGE_EXE to the built Conflict Studio bridge")
        root = self.organizer.root
        (root / "ModOrganizer.ini").write_text("[Plugins]\nCyberpunk%202077%20Support%20Plugin\\enforce_archive_load_order=false\n", encoding="utf-8")
        for mod, name, payload in (("High", "a.archive", b"a"), ("Low", "b.archive", b"b")):
            path = root / "mods" / mod / "archive" / "pc" / "mod" / name
            path.parent.mkdir(parents=True)
            data = struct.pack("<4sIQIQIQI", b"RDAR", 12, 172, 84, 0, 0, 256, 0)
            data += bytes(128) + struct.pack("<IIQIIIQqIIIII", 8, 56, 0, 1, 0, 0, 42, 0, 0, 0, 0, 0, 0) + payload * 20
            path.write_bytes(data)
        seed = root / "mods" / "High" / "archive" / "pc" / "mod" / "modlist.txt"
        seed.write_text("b.archive\na.archive\n", encoding="utf-8")
        self.organizer.activate_game()
        bridge = ArchiveBridge(executable)
        outputs = observe(bridge.result)
        failures = observe(bridge.failed)

        def request(operation, **arguments):
            loop = QEventLoop()
            bridge.result.connect(loop.quit)
            bridge.failed.connect(loop.quit)
            timer = QTimer()
            timer.setSingleShot(True)
            timer.timeout.connect(loop.quit)
            count = len(outputs)
            bridge.request(operation, **arguments)
            timer.start(15000)
            loop.exec()
            timer.stop()
            bridge.result.disconnect(loop.quit)
            bridge.failed.disconnect(loop.quit)
            self.assertEqual(failures, [])
            self.assertEqual(len(outputs), count + 1)
            return outputs[-1][2]

        try:
            context = self.plugin.context()
            state = request("scan", instanceRoot=context[0], profileModlistPath=context[1], activeProviders=[{"name": name, "priority": priority} for name, priority in context[2]])
            self.assertEqual(state["editableOrder"], ["b.archive", "a.archive"])
            self.assertEqual(len(state["palette"]), 5)
            self.assertFalse(Path(state["writeTarget"]).exists())
            selection = request("select", token=state["token"], selectedArchives=["b.archive"], order=state["editableOrder"], offset=0, limit=500)
            self.assertEqual(selection["totalSharedResources"], 1)
            self.assertEqual(selection["sharedResources"][0]["winnerArchive"], "b.archive")
            preview = request("preview", token=state["token"], order=["a.archive", "b.archive"])
            self.assertTrue(preview["canApply"])
            self.assertTrue(preview["winnerChangesKnown"])
            self.assertEqual(preview["totalWinnerDeltas"], 1)
            saved = request("apply", token=state["token"], order=["a.archive", "b.archive"])
            self.assertTrue(saved["canUndo"])
            self.assertEqual(Path(saved["writeTarget"]).read_text(encoding="utf-8"), "a.archive\nb.archive\n")
            self.assertEqual(seed.read_text(encoding="utf-8"), "b.archive\na.archive\n")
            restored = request("undo", token=saved["token"])
            self.assertFalse(restored["canUndo"])
            self.assertFalse(Path(restored["writeTarget"]).exists())
        finally:
            loop = QEventLoop()
            bridge.stopped.connect(loop.quit)
            bridge.close()
            timer = QTimer()
            timer.setSingleShot(True)
            timer.timeout.connect(loop.quit)
            timer.start(5000)
            if bridge.process.state() != QProcess.ProcessState.NotRunning:
                loop.exec()
            timer.stop()
            self.assertEqual(bridge.process.state(), QProcess.ProcessState.NotRunning)

    def test_window_close_cancels_and_drains_starting_actual_engine(self):
        executable = os.environ.get("MO2_ARCHIVE_BRIDGE_EXE")
        if not executable:
            self.skipTest("Set MO2_ARCHIVE_BRIDGE_EXE to the built Conflict Studio bridge")
        window, _ = self.window()
        bridge = self.plugin.bridge
        bridge.executable = executable
        context = self.plugin.context()
        bridge.request("scan", instanceRoot=context[0], profileModlistPath=context[1], activeProviders=[])
        event = QCloseEvent()
        QCoreApplication.sendEvent(window, event)
        self.assertFalse(event.isAccepted())
        self.assertTrue(bridge.closing)
        self.assertTrue(self.plugin.panel.closed)
        loop = QEventLoop()
        bridge.stopped.connect(loop.quit)
        timer = QTimer()
        timer.setSingleShot(True)
        timer.timeout.connect(loop.quit)
        timer.start(5000)
        if bridge.process.state() != QProcess.ProcessState.NotRunning:
            loop.exec()
        timer.stop()
        self.application.processEvents()
        self.assertEqual(bridge.process.state(), QProcess.ProcessState.NotRunning)
        self.assertFalse((self.organizer.profile_path / "archive-order" / "modlist.txt").exists())

    def test_actual_engine_cancel_then_refresh_reuses_same_connection(self):
        executable = os.environ.get("MO2_ARCHIVE_BRIDGE_EXE")
        if not executable:
            self.skipTest("Set MO2_ARCHIVE_BRIDGE_EXE to the built Conflict Studio bridge")
        root = self.organizer.root
        (root / "ModOrganizer.ini").write_text("[Plugins]\nCyberpunk%202077%20Support%20Plugin\\enforce_archive_load_order=false\n", encoding="utf-8")
        self.organizer.activate_game()
        bridge = ArchiveBridge(executable)
        failures = observe(bridge.failed)
        outputs = observe(bridge.result)
        context = self.plugin.context()
        arguments = {"instanceRoot": context[0], "profileModlistPath": context[1], "activeProviders": []}

        def wait_terminal():
            loop = QEventLoop()
            bridge.result.connect(loop.quit)
            bridge.failed.connect(loop.quit)
            timer = QTimer()
            timer.setSingleShot(True)
            timer.timeout.connect(loop.quit)
            timer.start(15000)
            loop.exec()
            timer.stop()
            bridge.result.disconnect(loop.quit)
            bridge.failed.disconnect(loop.quit)

        try:
            identifier = bridge.request("scan", **arguments)
            bridge.cancel()
            self.assertTrue(bridge.cancel_requested)
            wait_terminal()
            self.assertIsNone(bridge.pending)
            self.assertEqual(len(failures), 1)
            self.assertEqual(failures[0][0], identifier)
            self.assertTrue(failures[0][3])
            self.assertEqual(outputs, [])
            self.assertEqual(bridge.process.state(), QProcess.ProcessState.Running)
            process_id = bridge.process.processId()
            bridge.request("scan", **arguments)
            wait_terminal()
            self.assertIsNone(bridge.pending)
            self.assertEqual(len(failures), 1)
            self.assertEqual(len(outputs), 1)
            self.assertEqual(outputs[0][1], "scan")
            self.assertEqual(outputs[0][2]["profileName"], "First")
            self.assertEqual(bridge.process.processId(), process_id)
        finally:
            loop = QEventLoop()
            bridge.stopped.connect(loop.quit)
            bridge.close()
            timer = QTimer()
            timer.setSingleShot(True)
            timer.timeout.connect(loop.quit)
            timer.start(5000)
            if bridge.process.state() != QProcess.ProcessState.NotRunning:
                loop.exec()
            timer.stop()
            self.assertEqual(bridge.process.state(), QProcess.ProcessState.NotRunning)

    def test_rejected_close_restores_refresh_without_starting_a_helper(self):
        executable = os.environ.get("MO2_ARCHIVE_BRIDGE_EXE")
        if not executable:
            self.skipTest("Set MO2_ARCHIVE_BRIDGE_EXE to the built Conflict Studio bridge")

        class RejectCloseWindow(QMainWindow):
            def __init__(self):
                super().__init__()
                self.close_attempts = 0

            def closeEvent(self, event):
                self.close_attempts += 1
                event.ignore()

        window, tabs = self.window(RejectCloseWindow())
        window.setAttribute(Qt.WidgetAttribute.WA_DontShowOnScreen, True)
        window.show()
        tabs.setCurrentWidget(self.plugin.panel)
        bridge = self.plugin.bridge
        bridge.executable = executable
        context = self.plugin.context()
        bridge.request("scan", instanceRoot=context[0], profileModlistPath=context[1], activeProviders=[])
        window.close()
        self.assertTrue(self.plugin.panel.closed)
        loop = QEventLoop()
        timer = QTimer()
        timer.setSingleShot(True)
        timer.timeout.connect(loop.quit)
        bridge.stopped.connect(lambda: QTimer.singleShot(30, loop.quit))
        timer.start(5000)
        loop.exec()
        timer.stop()
        self.assertEqual(bridge.process.state(), QProcess.ProcessState.NotRunning)
        self.assertEqual(window.close_attempts, 1)
        self.assertTrue(window.isVisible())
        self.assertFalse(self.plugin.panel.closed)
        self.assertTrue(self.plugin.panel.isEnabled())
        self.assertTrue(self.plugin.panel.refresh_button.isEnabled())
        self.assertIn("Close cancelled", self.plugin.panel.status.text())
        self.assertIsNot(self.plugin.bridge, bridge)
        self.assertEqual(self.plugin.bridge.process.state(), QProcess.ProcessState.NotRunning)
        self.assertFalse(self.plugin.panel.timer.isActive())
        self.assertIsNone(self.plugin.panel.state)


if __name__ == "__main__":
    unittest.main()
