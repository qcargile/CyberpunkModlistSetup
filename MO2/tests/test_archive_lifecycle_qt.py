import ctypes
import os
import shutil
import subprocess
import threading
import unittest
from pathlib import Path
from unittest.mock import patch

import test_archives_qt as archive_tests

archive = archive_tests.archive
mobase = archive_tests.mobase
observe = archive_tests.observe

if mobase is not None:
    from PyQt6.QtCore import QDir, QEventLoop, QModelIndex, QProcess, Qt, QTimer
    from PyQt6.QtGui import QAction, QColor
    from PyQt6.QtWidgets import QMainWindow, QMenu, QMessageBox


@unittest.skipIf(mobase is None, "Requires MO2's native Python 3.12 and Qt libraries")
class ArchiveLifecycleTests(unittest.TestCase):
    setUp = archive_tests.ArchivesQtTests.setUp
    tearDown = archive_tests.ArchivesQtTests.tearDown
    window = archive_tests.ArchivesQtTests.window
    state = archive_tests.ArchivesQtTests.state
    loaded = archive_tests.ArchivesQtTests.loaded

    @classmethod
    def setUpClass(cls):
        archive_tests.ArchivesQtTests.setUpClass()
        cls.application = archive_tests.ArchivesQtTests.application

    def deliver(self, panel, data):
        panel.request_context = self.plugin.context()
        panel.operation_generation = panel.generation
        panel.needs_scan = False
        panel.result("scan", "scan", data)
        panel.timer.stop()
        self.wait_for(lambda: self.plugin.watcher.ready or not self.plugin.watcher.running())

    def reorder(self, panel, source, destination):
        model = panel.archive_model
        mime = model.mimeData([model.index(source, 1)])
        self.assertTrue(model.dropMimeData(mime, Qt.DropAction.MoveAction, destination, 0, QModelIndex()))
        panel.timer.stop()

    def preview(self, panel):
        panel.pending_preview = False
        panel.result("preview", "preview", {"changedArchives": [], "winnerDeltas": [], "totalWinnerDeltas": 0, "winnerChangesKnown": True, "truncated": False, "canApply": True, "blockedReason": None, "unknownImpact": "None"})
        panel.timer.stop()

    def dialog_button(self, text):
        def execute(dialog):
            button = next(button for button in dialog.buttons() if button.text() == text)
            button.click()
            return dialog.result()
        return patch.object(QMessageBox, "exec", execute)

    def wait_for(self, condition, timeout=1500):
        loop = QEventLoop()
        check = QTimer()
        check.setInterval(5)
        check.timeout.connect(lambda: loop.quit() if condition() else None)
        deadline = QTimer()
        deadline.setSingleShot(True)
        deadline.timeout.connect(loop.quit)
        check.start()
        deadline.start(timeout)
        if not condition():
            loop.exec()
        check.stop()
        deadline.stop()
        self.assertTrue(condition())

    def test_five_thousand_progress_updates_never_rebuild_owner_context(self):
        panel = self.loaded()
        with patch.object(self.plugin, "context", side_effect=AssertionError("Progress rebuilt the active profile")) as context:
            for completed in range(5000):
                panel.progress("scan", {"phase": "Checking conflicts", "completed": completed, "total": 5000})
            context.assert_not_called()
            self.assertTrue(panel.progress_timer.isActive())
            panel.show_progress()
            self.assertEqual(panel.progress_bar.value(), 4999)
            self.assertEqual(panel.status.text(), "Checking conflicts")
            panel.generation += 1
            panel.progress("stale", {"phase": "Old profile", "completed": 0, "total": 1})
            self.assertEqual(panel.last_progress["phase"], "Checking conflicts")

    def test_large_watch_file_is_not_hashed_on_the_host_thread(self):
        path = self.organizer.profile_path / "archive-order.txt"
        with path.open("wb") as stream:
            stream.truncate(10 * 1024 * 1024)
        with patch.object(Path, "open", side_effect=AssertionError("Large watch file was opened")):
            original = self.plugin.file_signature(str(path))
            metadata = path.stat()
            os.utime(path, ns=(metadata.st_atime_ns, metadata.st_mtime_ns + 1000000000))
            self.assertNotEqual(original, self.plugin.file_signature(str(path)))

    def test_small_watch_file_uses_content_when_size_and_time_match(self):
        path = self.organizer.profile_path / "archive-order.txt"
        path.write_bytes(b"one.archive")
        metadata = path.stat()
        original = self.plugin.file_signature(str(path))
        path.write_bytes(b"two.archive")
        os.utime(path, ns=(metadata.st_atime_ns, metadata.st_mtime_ns))
        self.assertNotEqual(original, self.plugin.file_signature(str(path)))

    def test_inventory_refresh_keeps_draft_and_appends_a_new_archive(self):
        panel = self.loaded()
        self.reorder(panel, 0, 2)
        panel.invalidate(rescan=False)
        refreshed = self.state()
        refreshed.update(token="scan-two", editableOrder=["a.archive", "b.archive", "c.archive"], effectiveOrder=["a.archive", "b.archive", "c.archive", "REDmod/c.archive"])
        refreshed["archiveRows"].insert(2, archive("c.archive"))
        self.deliver(panel, refreshed)
        self.assertEqual(panel.archive_model.editable_order, ["b.archive", "a.archive", "c.archive"])
        self.assertFalse(panel.pending_order_conflict)
        self.assertTrue(panel.pending_preview)
        self.assertIn("Unsaved order kept", panel.status.text())

    def test_each_profile_restores_its_own_draft(self):
        panel = self.loaded()
        first_path = panel.state["profilePath"]
        self.reorder(panel, 0, 2)
        panel.invalidate(rescan=False)
        self.organizer.profile_path = self.organizer.root / "profiles" / "Other"
        self.organizer.profile_path.mkdir()
        (self.organizer.profile_path / "modlist.txt").write_text("+High\n+Low\n", encoding="utf-8")
        other = self.state()
        other.update(token="other-scan", profileName="Other", editableOrder=["x.archive", "y.archive"], effectiveOrder=["x.archive", "y.archive"], archiveRows=[archive("x.archive"), archive("y.archive")])
        self.deliver(panel, other)
        self.assertEqual(panel.archive_model.editable_order, ["x.archive", "y.archive"])
        self.reorder(panel, 0, 2)
        other_path = panel.state["profilePath"]
        panel.invalidate(rescan=False)
        self.organizer.profile_path = Path(first_path).parent
        self.deliver(panel, {**self.state(), "token": "first-return"})
        self.assertEqual(panel.archive_model.editable_order, ["b.archive", "a.archive"])
        self.assertEqual(panel.drafts[other_path]["order"], ["y.archive", "x.archive"])
        self.assertEqual(panel.state["profilePath"], first_path)

    def test_external_order_change_requires_a_choice_and_saved_choice_resets_draft(self):
        panel = self.loaded()
        initial = self.state()
        initial.update(token="three", editableOrder=["a.archive", "b.archive", "c.archive"], effectiveOrder=["a.archive", "b.archive", "c.archive"], archiveRows=[archive("a.archive"), archive("b.archive"), archive("c.archive")])
        self.deliver(panel, initial)
        self.reorder(panel, 0, 3)
        panel.invalidate(rescan=False)
        changed = {**initial, "token": "external", "editableOrder": ["c.archive", "b.archive", "a.archive"], "effectiveOrder": ["c.archive", "b.archive", "a.archive"]}
        self.deliver(panel, changed)
        self.assertTrue(panel.pending_order_conflict)
        self.assertEqual(panel.archive_model.editable_order, ["b.archive", "c.archive", "a.archive"])
        self.preview(panel)
        with self.dialog_button("Use saved order"), patch.object(self.plugin.bridge, "request") as request:
            panel.save()
            request.assert_not_called()
        self.assertEqual(panel.archive_model.editable_order, changed["editableOrder"])
        self.assertFalse(panel.pending_order_conflict)
        self.assertFalse(panel.has_draft())
        self.assertNotIn(changed["profilePath"], panel.drafts)
        panel.timer.stop()

    def conflicting_draft(self):
        panel = self.loaded()
        initial = self.state()
        initial.update(token="initial-three", editableOrder=["a.archive", "b.archive", "c.archive"], effectiveOrder=["a.archive", "b.archive", "c.archive"], archiveRows=[archive("a.archive"), archive("b.archive"), archive("c.archive")])
        self.deliver(panel, initial)
        self.reorder(panel, 0, 3)
        panel.invalidate(rescan=False)
        changed = {**initial, "token": "changed-order", "editableOrder": ["c.archive", "b.archive", "a.archive"], "effectiveOrder": ["c.archive", "b.archive", "a.archive"]}
        self.deliver(panel, changed)
        self.assertTrue(panel.pending_order_conflict)
        return panel, initial, changed

    def test_unresolved_order_conflict_survives_another_refresh_and_profile_return(self):
        panel, initial, changed = self.conflicting_draft()
        first_path = initial["profilePath"]
        panel.invalidate(rescan=False)
        self.deliver(panel, {**changed, "token": "second-refresh"})
        self.assertTrue(panel.pending_order_conflict)
        self.assertEqual(panel.drafts[first_path]["baseline"], initial["editableOrder"])
        panel.invalidate(rescan=False)
        self.organizer.profile_path = self.organizer.root / "profiles" / "Other"
        self.organizer.profile_path.mkdir()
        (self.organizer.profile_path / "modlist.txt").write_text("+High\n+Low\n", encoding="utf-8")
        self.deliver(panel, {**self.state(), "token": "other-profile", "profileName": "Other"})
        self.assertFalse(panel.pending_order_conflict)
        panel.invalidate(rescan=False)
        self.organizer.profile_path = Path(first_path).parent
        self.deliver(panel, {**changed, "token": "returned-to-first"})
        self.assertTrue(panel.pending_order_conflict)
        self.assertEqual(panel.archive_model.editable_order, ["b.archive", "c.archive", "a.archive"])
        self.assertEqual(panel.drafts[first_path]["baseline"], initial["editableOrder"])

    def test_save_dialog_does_not_authorize_an_invalidated_or_replaced_scan(self):
        panel, initial, changed = self.conflicting_draft()
        for replacement in (False, True):
            with self.subTest(replacement_scan=replacement):
                self.preview(panel)
                self.assertTrue(panel.save_button.isEnabled())
                previous_generation = panel.generation

                def execute(dialog):
                    panel.invalidate(rescan=False)
                    if replacement:
                        self.deliver(panel, {**changed, "token": "replacement-scan"})
                    next(button for button in dialog.buttons() if button.text() == "Save my draft").click()
                    return dialog.result()

                with patch.object(QMessageBox, "exec", execute), patch.object(self.plugin.bridge, "request") as request:
                    panel.save()
                    request.assert_not_called()
                self.assertGreater(panel.generation, previous_generation)
                self.assertIn("changed again", panel.status.text())
                self.assertEqual(panel.drafts[initial["profilePath"]]["baseline"], initial["editableOrder"])
                if replacement:
                    self.assertTrue(panel.pending_order_conflict)
                    self.assertEqual(panel.state["token"], "replacement-scan")
                else:
                    self.assertIsNone(panel.state)
                    self.deliver(panel, {**changed, "token": "scan-after-dialog"})
                    self.assertTrue(panel.pending_order_conflict)

    def test_missing_profile_override_can_save_an_unchanged_previewed_order(self):
        panel = self.loaded()
        data = {**self.state(), "token": "needs-write", "needsOrderWrite": True}
        self.deliver(panel, data)
        self.assertFalse(panel.has_draft())
        self.assertTrue(panel.pending_preview)
        self.assertFalse(panel.save_button.isEnabled())
        self.preview(panel)
        self.assertTrue(panel.save_button.isEnabled())
        with patch.object(self.plugin.bridge, "request") as request:
            panel.save()
            request.assert_called_once_with("apply", token="needs-write", order=data["editableOrder"])

    def watch_archive_change(self, mutation, expected):
        panel = self.loaded()
        directory = self.organizer.root / "mods" / "High" / "archive" / "pc" / "mod"
        directory.mkdir(parents=True)
        path = directory / "a.archive"
        path.write_bytes(b"original")
        data = self.state()
        data["archiveRows"][0]["physicalPath"] = str(path)
        data["archiveDirectories"] = [str(directory)]
        self.deliver(panel, data)
        native_events = observe(self.plugin.watcher.changed)
        with patch.object(self.plugin, "changed", wraps=self.plugin.changed) as changed:
            mutation(directory, path)
            if not expected:
                profile = self.organizer.profile_path / "modlist.txt"
                profile.write_bytes(profile.read_bytes())
            self.wait_for(lambda: bool(native_events))
            self.assertEqual(bool(changed.call_count), expected)
            self.assertEqual(panel.state is None, expected)
        panel.timer.stop()

    def test_native_file_watcher_invalidates_an_added_archive(self):
        self.watch_archive_change(lambda directory, path: (directory / "new.archive").write_bytes(b"new"), True)

    def test_native_file_watcher_invalidates_a_deleted_archive(self):
        self.watch_archive_change(lambda directory, path: path.unlink(), True)

    def test_watching_an_archive_does_not_prevent_removing_its_mod_directory(self):
        panel = self.loaded()
        mod = self.organizer.root / "mods" / "High"
        directory = mod / "archive" / "pc" / "mod"
        directory.mkdir(parents=True)
        path = directory / "a.archive"
        path.write_bytes(b"original")
        data = self.state()
        data["archiveRows"][0]["physicalPath"] = str(path)
        data["archiveDirectories"] = [str(directory)]
        self.deliver(panel, data)
        renamed = mod.with_name("Renamed")
        mod.rename(renamed)
        mod = renamed
        shutil.rmtree(mod)
        self.assertFalse(mod.exists())
        self.wait_for(lambda: panel.state is None)
        panel.timer.stop()

    def test_watching_a_missing_archive_directory_does_not_lock_its_existing_parent(self):
        panel = self.loaded()
        mod = self.organizer.root / "mods" / "High"
        parent = mod / "archive"
        parent.mkdir(parents=True)
        data = self.state()
        data["archiveRows"] = []
        data["archiveDirectories"] = [str(parent / "pc" / "mod")]
        self.deliver(panel, data)
        self.application.processEvents()
        parent.rmdir()
        mod.rmdir()
        self.assertFalse(mod.exists())

    def test_native_file_watcher_invalidates_written_archive_bytes(self):
        self.watch_archive_change(lambda directory, path: path.write_bytes(b"changed"), True)

    def test_native_file_watcher_ignores_an_unrelated_file_in_the_archive_directory(self):
        self.watch_archive_change(lambda directory, path: (directory / "readme.txt").write_text("notes", encoding="utf-8"), False)

    def archive_watch(self, root=None):
        panel = self.loaded()
        mod = (root or self.organizer.root / "mods") / "High"
        directory = mod / "archive" / "pc" / "mod"
        directory.mkdir(parents=True)
        path = directory / "a.archive"
        path.write_bytes(b"original")
        data = self.state()
        data["archiveRows"] = [{**archive("a.archive"), "physicalPath": str(path)}]
        data["archiveDirectories"] = [str(directory)]
        self.deliver(panel, data)
        return panel, mod, directory, path, data

    def test_archive_directory_prefixes_can_be_moved_and_deleted(self):
        panel, mod, directory, path, data = self.archive_watch()
        for relative in ("archive/pc/mod", "archive/pc", "archive"):
            with self.subTest(directory=relative):
                original = mod / relative
                moved = original.with_name(original.name + "-moved")
                original.rename(moved)
                shutil.rmtree(moved)
                self.wait_for(lambda: panel.state is None)
                directory.mkdir(parents=True)
                path.write_bytes(b"recreated")
                self.deliver(panel, data)
        panel.timer.stop()

    def test_recreated_mod_and_archive_directories_are_observed(self):
        panel, mod, directory, path, data = self.archive_watch()
        shutil.rmtree(mod)
        self.wait_for(lambda: panel.state is None)
        self.deliver(panel, {**data, "archiveRows": []})
        directory.mkdir(parents=True)
        path.write_bytes(b"recreated")
        self.wait_for(lambda: panel.state is None)
        self.deliver(panel, data)
        path.write_bytes(b"edited")
        self.wait_for(lambda: panel.state is None)
        panel.timer.stop()

    def test_recreated_mods_root_is_observed_after_refresh(self):
        panel, mod, directory, path, data = self.archive_watch()
        mods = self.organizer.root / "mods"
        moved = mods.with_name("MovedMods")
        mods.rename(moved)
        self.wait_for(lambda: panel.state is None)
        directory.mkdir(parents=True)
        path.write_bytes(b"recreated")
        self.deliver(panel, data)
        path.write_bytes(b"edited")
        self.wait_for(lambda: panel.state is None)
        shutil.rmtree(moved)
        panel.timer.stop()

    def test_missing_mods_root_is_observed_when_created(self):
        panel = self.loaded()
        mods = self.organizer.root / "mods"
        mods.rmdir()
        directory = mods / "High" / "archive" / "pc" / "mod"
        data = {**self.state(), "archiveRows": [], "archiveDirectories": [str(directory)]}
        self.deliver(panel, data)
        directory.mkdir(parents=True)
        (directory / "new.archive").write_bytes(b"new")
        self.wait_for(lambda: panel.state is None)
        panel.timer.stop()

    def test_profile_directory_can_be_renamed_without_a_lease(self):
        panel = self.loaded()
        self.wait_for(lambda: self.plugin.watcher.ready)
        original = self.organizer.profile_path
        moved = original.with_name("Renamed")
        original.rename(moved)
        self.wait_for(lambda: panel.state is None)
        self.organizer.profile_path = moved
        self.deliver(panel, self.state())
        (moved / "modlist.txt").write_text("+Changed\n", encoding="utf-8")
        self.wait_for(lambda: panel.state is None)
        panel.timer.stop()

    def test_atomic_order_replacement_is_observed_after_a_profile_change(self):
        panel = self.loaded()
        self.organizer.profile_path = self.organizer.root / "profiles" / "Second"
        self.organizer.profile_path.mkdir()
        (self.organizer.profile_path / "modlist.txt").write_text("+High\n", encoding="utf-8")
        order = self.organizer.profile_path / "archive-order.txt"
        order.write_text("a.archive\nb.archive\n", encoding="utf-8")
        data = {**self.state(), "orderSource": str(order)}
        self.deliver(panel, data)
        temporary = order.with_suffix(".tmp")
        temporary.write_text("b.archive\na.archive\n", encoding="utf-8")
        temporary.replace(order)
        self.wait_for(lambda: panel.state is None)
        self.deliver(panel, data)
        order.unlink()
        self.wait_for(lambda: panel.state is None)
        panel.timer.stop()

    def test_native_archive_content_changes_with_unchanged_metadata_are_observed(self):
        def change(directory, path):
            metadata = path.stat()
            path.write_bytes(b"modified")
            os.utime(path, ns=(metadata.st_atime_ns, metadata.st_mtime_ns))
        self.watch_archive_change(change, True)

    def test_unrelated_archive_directory_file_changes_do_not_read_order_files(self):
        panel, mod, directory, path, data = self.archive_watch()
        unrelated = directory / "readme.txt"
        unrelated.write_text("one", encoding="utf-8")
        self.application.processEvents()
        with patch.object(self.plugin, "file_signature", side_effect=AssertionError("Unrelated event read order files")), patch.object(self.plugin, "changed") as changed:
            unrelated.write_text("two", encoding="utf-8")
            unrelated.unlink()
            loop = QEventLoop()
            QTimer.singleShot(100, loop.quit)
            loop.exec()
            changed.assert_not_called()
        self.assertIsNotNone(panel.state)

    def test_native_watch_failure_disables_stale_order_and_refresh_recovers(self):
        panel, mod, directory, path, data = self.archive_watch()
        watcher = self.plugin.watcher
        watcher.stop()
        handle = watcher.api.CreateFileW(str(self.organizer.root / "mods"), 1, 0, None, 3, 0x02000000, None)
        self.assertNotEqual(handle, ctypes.c_void_p(-1).value)
        try:
            self.deliver(panel, data)
            self.wait_for(lambda: panel.state is None)
            self.assertFalse(watcher.running())
            self.assertFalse(panel.timer.isActive())
            self.assertFalse(panel.save_button.isEnabled())
            self.assertTrue(panel.refresh_button.isEnabled())
            self.assertIn("Archive monitoring stopped", panel.status.text())
        finally:
            watcher.api.CloseHandle(handle)
        self.deliver(panel, data)
        self.assertTrue(watcher.running())
        path.write_bytes(b"recovered")
        self.wait_for(lambda: panel.state is None)
        panel.timer.stop()

    def test_linked_archive_target_changes_are_observed_without_locking_the_target(self):
        external = self.organizer.root / "External"
        external.mkdir()
        target = external / "High"
        target.mkdir()
        junction = self.organizer.root / "mods" / "High"
        subprocess.run(["cmd", "/c", "mklink", "/J", str(junction), str(target)], check=True, capture_output=True)
        panel, mod, directory, path, data = self.archive_watch()
        path.write_bytes(b"changed")
        self.wait_for(lambda: panel.state is None)
        self.deliver(panel, data)
        physical = target / "archive" / "pc" / "mod" / "a.archive"
        physical.write_bytes(b"external")
        self.wait_for(lambda: panel.state is None)
        self.deliver(panel, data)
        moved = target.with_name("Moved")
        target.rename(moved)
        self.wait_for(lambda: panel.state is None)
        shutil.rmtree(moved)
        panel.timer.stop()

    def test_native_watch_cost_is_bounded_by_roots_and_stops_when_disabled(self):
        panel, mod, directory, path, data = self.archive_watch()
        rows = [{**archive(f"{index}.archive"), "physicalPath": str(directory / f"{index}.archive")} for index in range(5000)]
        self.deliver(panel, {**data, "archiveRows": rows})
        watcher = self.plugin.watcher
        worker = watcher.thread
        self.assertEqual(sum(thread.name == "ChromeBloodArchiveWatch" for thread in threading.enumerate()), 1)
        self.assertLessEqual(len(watcher.specification[0]), 8)
        self.organizer.enabled = False
        self.plugin.disabled()
        self.assertFalse(worker.is_alive())
        self.assertFalse(watcher.running())
        self.assertIsNone(watcher.stop_event)
        mod.rename(mod.with_name("Disabled"))
        self.assertEqual(sum(thread.name == "ChromeBloodArchiveWatch" for thread in threading.enumerate()), 0)

    def test_unchanged_watch_state_reuses_the_ready_worker(self):
        panel, mod, directory, path, data = self.archive_watch()
        worker = self.plugin.watcher.thread
        self.deliver(panel, data)
        self.assertIs(self.plugin.watcher.thread, worker)

    def test_native_watch_startup_keeps_the_qt_event_loop_responsive(self):
        from chrome_blood_archives.watcher import DirectoryRead
        panel, mod, directory, path, data = self.archive_watch()
        watcher = self.plugin.watcher
        specification = watcher.specification
        release = threading.Event()
        original = DirectoryRead.start
        def delayed_start(read):
            if not release.wait(2):
                raise OSError("The Qt event loop did not release startup.")
            original(read)
        with patch.object(DirectoryRead, "start", delayed_start):
            watcher.configure(*specification, force=True)
            self.assertFalse(watcher.ready)
            QTimer.singleShot(10, release.set)
            self.wait_for(lambda: watcher.ready or not watcher.running())
            self.assertTrue(release.is_set())
            self.assertTrue(watcher.ready)

    def test_native_watch_shutdown_keeps_the_qt_event_loop_responsive(self):
        from chrome_blood_archives.watcher import DirectoryRead
        self.archive_watch()
        watcher = self.plugin.watcher
        worker = watcher.thread
        release = threading.Event()
        original = DirectoryRead.close
        def delayed_close(read):
            release.wait(2)
            original(read)
        with patch.object(DirectoryRead, "close", delayed_close):
            QTimer.singleShot(10, release.set)
            watcher.stop()
            self.assertTrue(release.is_set())
            self.assertFalse(worker.is_alive())

    def test_disabling_discards_queued_native_changes(self):
        panel, mod, directory, path, data = self.archive_watch()
        with patch.object(self.plugin, "changed") as changed:
            path.write_bytes(b"queued")
            self.organizer.enabled = False
            self.plugin.disabled()
            self.application.processEvents()
            changed.assert_not_called()

    def test_destroyed_window_drains_native_watch_worker(self):
        self.archive_watch()
        worker = self.plugin.watcher.thread
        self.plugin.window.deleteLater()
        archive_tests.QCoreApplication.sendPostedEvents(None, archive_tests.QEvent.Type.DeferredDelete)
        self.application.processEvents()
        self.assertFalse(worker.is_alive())
        self.assertFalse(self.plugin.watcher.running())
        self.plugin.bridge = None

    def test_accepted_close_drains_native_watch_worker(self):
        self.archive_watch()
        worker = self.plugin.watcher.thread
        self.assertTrue(self.plugin.window.close())
        self.assertFalse(worker.is_alive())
        self.application.processEvents()
        self.assertFalse(self.plugin.watcher.running())

    def test_before_run_blocks_active_save_for_the_game_and_allows_tools(self):
        self.loaded()
        game = self.organizer.managedGame()
        binary = game.gameDirectory().absoluteFilePath(game.binaryName())
        self.plugin.bridge.pending = {"id": "saving", "operation": "apply"}
        with patch.object(QMessageBox, "information") as information:
            self.assertFalse(self.plugin.before_run(binary, QDir(), ""))
            self.assertTrue(self.plugin.before_run(str(self.organizer.root / "tool.exe"), QDir(), ""))
            information.assert_called_once()
        self.plugin.bridge.pending = None
        self.assertTrue(self.plugin.before_run(binary, QDir(), ""))

    def test_before_run_warns_about_drafts_and_only_launches_saved_order_on_request(self):
        panel = self.loaded()
        self.reorder(panel, 0, 2)
        game = self.organizer.managedGame()
        binary = game.gameDirectory().absoluteFilePath(game.binaryName())
        with self.dialog_button("Launch saved order"):
            self.assertTrue(self.plugin.before_run(binary, QDir(), ""))
        with self.dialog_button("Cancel"):
            self.assertFalse(self.plugin.before_run(binary, QDir(), ""))
        with self.dialog_button("Return to save"), patch.object(self.plugin, "display") as display:
            self.assertFalse(self.plugin.before_run(binary, QDir(), ""))
            display.assert_called_once()

    def test_close_warns_for_current_and_cached_drafts_and_keeps_both_on_cancel(self):
        panel = self.loaded()
        self.reorder(panel, 0, 2)
        cached = str(self.organizer.root / "profiles" / "Other" / "modlist.txt")
        panel.drafts[cached] = {"order": ["y.archive", "x.archive"], "baseline": ["x.archive", "y.archive"]}
        window = self.plugin.window
        window.setAttribute(Qt.WidgetAttribute.WA_DontShowOnScreen, True)
        window.show()
        with self.dialog_button("Keep MO2 open"), patch.object(self.plugin.bridge, "request") as request:
            self.assertFalse(window.close())
            request.assert_not_called()
        self.assertTrue(window.isVisible())
        self.assertFalse(panel.closed)
        self.assertEqual(len(panel.drafts), 2)
        with self.dialog_button("Close without saving"):
            self.assertTrue(window.close())
        self.assertEqual(len(panel.drafts), 2)
        self.assertFalse((self.organizer.profile_path / "archive-order" / "modlist.txt").exists())

    def test_native_close_cancellation_restores_tab_and_preserves_draft(self):
        saved_indexes = []

        class RejectWindow(QMainWindow):
            def closeEvent(window, event):
                saved_indexes.append(self.plugin.tabs.currentIndex())
                event.ignore()

        window, tabs = self.window(RejectWindow())
        panel = self.plugin.panel
        self.deliver(panel, self.state())
        tabs.setCurrentIndex(2)
        tabs.setCurrentWidget(panel)
        self.reorder(panel, 0, 2)
        window.setAttribute(Qt.WidgetAttribute.WA_DontShowOnScreen, True)
        window.show()
        with self.dialog_button("Close without saving"):
            self.assertFalse(window.close())
        self.application.processEvents()
        panel.timer.stop()
        self.assertEqual(saved_indexes, [1])
        self.assertIs(tabs.currentWidget(), panel)
        self.assertEqual(panel.archive_model.editable_order, ["b.archive", "a.archive"])
        self.assertFalse(self.plugin.lifetime.close_confirmed)

    def test_native_pane_index_is_restored_before_close_and_stable_name_is_saved(self):
        observed = []

        class SaveWindow(QMainWindow):
            def closeEvent(window, event):
                observed.append((self.plugin.tabs.currentIndex(), self.plugin.tabs.currentWidget().objectName()))
                event.accept()

        window, tabs = self.window(SaveWindow())
        tabs.setCurrentIndex(3)
        self.assertTrue(window.close())
        self.assertEqual(observed, [(2, "downloadTab")])
        self.assertEqual(self.organizer.persistent(self.plugin.name(), "selected_pane"), "downloadTab")
        self.plugin.install_tab(scan_visible=False)
        self.assertEqual(tabs.currentWidget().objectName(), "downloadTab")
        self.assertEqual(tabs.currentIndex(), 3)

    def test_saved_archive_pane_is_selected_by_name_on_startup(self):
        self.organizer.setPersistent(self.plugin.name(), "selected_pane", "chromeBloodArchivesTab")
        window, tabs = self.window()
        self.assertIs(tabs.currentWidget(), self.plugin.panel)
        self.plugin.panel.timer.stop()
        self.plugin.detach_tab()
        self.assertEqual(tabs.currentWidget().objectName(), "dataTab")
        self.assertEqual(tabs.currentIndex(), 0)

    def test_file_exit_uses_close_guard_and_native_action_returns_when_disabled(self):
        window = QMainWindow()
        menu = QMenu(window)
        menu.setObjectName("menuFile")
        native = QAction("E&xit", window)
        native.setObjectName("actionExit")
        menu.addAction(native)
        native_calls = []
        native.triggered.connect(lambda: native_calls.append(True))
        self.window(window)
        panel = self.plugin.panel
        self.deliver(panel, self.state())
        self.reorder(panel, 0, 2)
        window.setAttribute(Qt.WidgetAttribute.WA_DontShowOnScreen, True)
        window.show()
        self.assertFalse(native.isVisible())
        with self.dialog_button("Keep MO2 open"):
            self.plugin.exit_action.trigger()
        self.assertTrue(window.isVisible())
        self.assertEqual(native_calls, [])
        self.organizer.enabled = False
        self.plugin.disabled()
        self.assertTrue(native.isVisible())
        self.assertFalse(self.plugin.exit_action.isVisible())

    def test_disabled_plugin_does_not_prompt_or_change_native_pane_on_close(self):
        panel = self.loaded()
        self.reorder(panel, 0, 2)
        self.organizer.enabled = False
        self.plugin.disabled()
        tabs = self.plugin.tabs
        tabs.setCurrentIndex(2)
        persisted = dict(self.organizer.persisted)
        with patch.object(QMessageBox, "exec", side_effect=AssertionError("Disabled plugin prompted")):
            self.assertTrue(self.plugin.window.close())
        self.assertTrue(panel.drafts)
        self.assertEqual(tabs.currentWidget().objectName(), "downloadTab")
        self.assertEqual(tabs.count(), 3)
        self.assertEqual(self.organizer.persisted, persisted)

    def test_disabled_plugin_still_drains_a_starting_helper_on_close(self):
        executable = os.environ.get("MO2_ARCHIVE_BRIDGE_EXE")
        if not executable:
            self.skipTest("Set MO2_ARCHIVE_BRIDGE_EXE to the built Conflict Studio bridge")
        panel = self.loaded()
        self.reorder(panel, 0, 2)
        bridge = self.plugin.bridge
        bridge.executable = executable
        context = self.plugin.context()
        bridge.request("scan", instanceRoot=context[0], profileModlistPath=context[1], activeProviders=[])
        self.organizer.enabled = False
        self.plugin.disabled()
        with patch.object(QMessageBox, "exec", side_effect=AssertionError("Disabled plugin prompted")):
            self.assertFalse(self.plugin.window.close())
            self.wait_for(lambda: bridge.process.state() == QProcess.ProcessState.NotRunning, timeout=5000)
            self.application.processEvents()
        self.assertTrue(panel.closed)
        self.assertTrue(panel.drafts)
        self.assertEqual(self.plugin.tabs.count(), 3)
        self.assertIs(self.plugin.bridge, bridge)

    def test_application_exit_drains_starting_actual_engine_before_returning(self):
        executable = os.environ.get("MO2_ARCHIVE_BRIDGE_EXE")
        if not executable:
            self.skipTest("Set MO2_ARCHIVE_BRIDGE_EXE to the built Conflict Studio bridge")
        self.window()
        bridge = self.plugin.bridge
        bridge.executable = executable
        context = self.plugin.context()
        bridge.request("scan", instanceRoot=context[0], profileModlistPath=context[1], activeProviders=[])
        stopped = observe(bridge.stopped)
        QTimer.singleShot(0, lambda: self.application.exit(7))
        self.assertEqual(self.application.exec(), 7)
        self.assertTrue(stopped)
        self.assertTrue(bridge.closing)
        self.assertEqual(bridge.process.state(), QProcess.ProcessState.NotRunning)
        self.assertTrue(self.plugin.lifetime.exit_committed)
        self.assertEqual(self.plugin.tabs.count(), 3)

    def test_busy_close_cancellation_recovers_cached_draft_without_another_helper(self):
        executable = os.environ.get("MO2_ARCHIVE_BRIDGE_EXE")
        if not executable:
            self.skipTest("Set MO2_ARCHIVE_BRIDGE_EXE to the built Conflict Studio bridge")

        class RejectWindow(QMainWindow):
            def closeEvent(window, event):
                event.ignore()

        window, tabs = self.window(RejectWindow())
        panel = self.plugin.panel
        self.deliver(panel, self.state())
        self.reorder(panel, 0, 2)
        tabs.setCurrentWidget(panel)
        window.setAttribute(Qt.WidgetAttribute.WA_DontShowOnScreen, True)
        window.show()
        bridge = self.plugin.bridge
        bridge.executable = executable
        context = self.plugin.context()
        bridge.request("scan", instanceRoot=context[0], profileModlistPath=context[1], activeProviders=[])
        with self.dialog_button("Close without saving"):
            self.assertFalse(window.close())
        self.wait_for(lambda: self.plugin.bridge is not bridge and not panel.closed, timeout=5000)
        self.assertEqual(panel.drafts[context[1]]["order"], ["b.archive", "a.archive"])
        self.assertFalse(panel.timer.isActive())
        self.assertEqual(self.plugin.bridge.process.state(), QProcess.ProcessState.NotRunning)
        self.assertIs(tabs.currentWidget(), panel)
        with self.dialog_button("Keep MO2 open"):
            self.assertFalse(window.close())

    def test_real_native_tree_keeps_the_core_relationship_background_and_strip(self):
        panel = self.loaded()
        window = panel.window()
        window.setAttribute(Qt.WidgetAttribute.WA_DontShowOnScreen, True)
        theme = os.environ.get("MO2_ARCHIVES_THEME_QSS")
        if theme:
            window.setStyleSheet(Path(theme).read_text(encoding="utf-8-sig"))
        window.resize(800, 500)
        window.show()
        self.plugin.tabs.setCurrentWidget(panel)
        panel.set_selected_archives(["a.archive"])
        panel.archive_model.set_relationships([{"archiveName": "b.archive", "tone": "Wins", "colorHex": "#42E58B", "backgroundHex": "#08251B"}])
        panel.timer.stop()
        self.application.processEvents()
        self.assertEqual(panel.selection_names, ["a.archive"])
        self.assertFalse(panel.archives.selectionModel().isSelected(panel.archive_model.index(1, 1)))
        image = panel.archives.viewport().grab().toImage()
        name_rect = panel.archives.visualRect(panel.archive_model.index(1, 1))
        order_rect = panel.archives.visualRect(panel.archive_model.index(1, 0))
        ratio = image.devicePixelRatio()
        self.assertEqual(image.pixelColor(round((name_rect.right() - 12) * ratio), round(name_rect.center().y() * ratio)).name(), "#08251b")
        self.assertEqual(image.pixelColor(round((order_rect.left() + 1) * ratio), round(order_rect.center().y() * ratio)), QColor("#42E58B"))


if __name__ == "__main__":
    unittest.main()
