import sys
import json
import threading
import time
import unittest
import tempfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch


sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "plugins"))

try:
    import mobase
    from PyQt6.QtCore import QCoreApplication, QEvent, QEventLoop, QObject, QTimer, QUrl
    from PyQt6.QtNetwork import QNetworkRequest
    from PyQt6.QtWidgets import QApplication, QMessageBox, QWidget
    from chrome_blood_update_notice import createPlugin
    from chrome_blood_update_notice import notice
    from chrome_blood_update_notice.release import MAX_BYTES, RELEASE_URL, Notice
except ImportError:
    mobase = None


class ResponseHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.server.delay:
            time.sleep(self.server.delay)
        try:
            self.send_response(self.server.status)
            self.send_header("Content-Length", str(len(self.server.body)))
            self.end_headers()
            self.wfile.write(self.server.body)
        except OSError:
            pass

    def log_message(self, *_):
        pass


class Organizer:
    def __init__(self, directory):
        self.directory = directory
        self.enabled = True
        self.preferences = {}
        self.game = SimpleNamespace(gameShortName=lambda: "cyberpunk2077")

    def managedGame(self):
        return self.game

    def basePath(self):
        return self.directory

    def isPluginEnabled(self, name):
        return self.enabled

    def persistent(self, plugin_name, key, default=None):
        return self.preferences.get((plugin_name, key), default)

    def setPersistent(self, plugin_name, key, value, sync=True):
        self.preferences[(plugin_name, key)] = value

    def onUserInterfaceInitialized(self, callback):
        self.ready = callback

    def onPluginDisabled(self, name, callback):
        self.disabled = callback

    def onPluginEnabled(self, name, callback):
        self.activated = callback

    def disable(self):
        self.enabled = False
        self.disabled()

    def enable(self):
        self.enabled = True
        self.activated()


@unittest.skipIf(mobase is None, "Requires the MO2 Python 3.12 bindings and Qt libraries")
class NoticeQtTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.application = QApplication.instance() or QApplication([])
        cls.application.setQuitOnLastWindowClosed(False)

    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.organizer = Organizer(directory.name)
        show_patch = patch.object(QMessageBox, "show")
        self.show_dialog = show_patch.start()
        self.addCleanup(show_patch.stop)
        settings = {"ModListName": "Chrome & Blood", "MachineUrl": "Chrome_and_Blood/SchoolBoyQ", "Version": "4.2.1"}
        (Path(directory.name) / "Chrome & Blood.compiler_settings").write_text(json.dumps(settings), encoding="utf-8")
        self.slot_errors = []
        previous_hook = sys.excepthook
        sys.excepthook = lambda kind, value, trace: self.slot_errors.append((kind, value))
        self.addCleanup(setattr, sys, "excepthook", previous_hook)

    def plugin(self):
        plugin = createPlugin()
        self.assertTrue(plugin.init(self.organizer))
        self.addCleanup(plugin._close)
        return plugin

    def tearDown(self):
        QCoreApplication.sendPostedEvents(None, QEvent.Type.DeferredDelete)
        self.application.processEvents()
        self.assertEqual(self.slot_errors, [])

    def test_factory_is_notification_tool(self):
        plugin = self.plugin()
        self.assertIsInstance(plugin, mobase.IPluginTool)
        self.assertNotIsInstance(plugin, mobase.IPluginDiagnose)
        self.assertEqual(plugin.name(), "Chrome & Blood Release Notice")
        self.assertEqual(plugin.displayName(), "Preview Chrome & Blood update notice")
        self.assertEqual(plugin.settings(), [])
        self.assertIsNone(plugin._dialog)

    def test_init_before_managed_game_preserves_ready_callback_and_preview(self):
        self.organizer.game = None
        plugin = self.plugin()
        self.assertTrue(callable(self.organizer.ready))
        self.assertFalse(plugin._active())
        self.organizer.game = SimpleNamespace(gameShortName=lambda: "cyberpunk2077")
        window = QWidget()
        self.organizer.ready(window)
        with patch.object(notice, "installed_version", return_value=None):
            self.application.processEvents()
        plugin.display()
        self.assertIs(plugin._window, window)
        self.assertIsNotNone(plugin._dialog)
        self.assertIn("4.2.1", plugin._dialog.text())

    def test_other_game_never_reads_metadata_or_requests_release(self):
        self.organizer.game = None
        plugin = self.plugin()
        self.organizer.game = SimpleNamespace(gameShortName=lambda: "skyrimse")
        with patch.object(notice, "installed_version") as read_metadata:
            self.organizer.ready(QWidget())
            self.application.processEvents()
            plugin.display()
            read_metadata.assert_not_called()
        self.assertFalse(plugin._started)
        self.assertIsNone(plugin._reply)
        self.assertIsNone(plugin._dialog)

    def test_shutdown_cancels_queued_start(self):
        plugin = self.plugin()
        window = QObject()
        plugin._ready(window)
        plugin._close()
        self.application.processEvents()
        self.assertFalse(plugin._started)
        self.assertIsNone(plugin._reply)

    def test_destroyed_window_cancels_queued_start(self):
        plugin = self.plugin()
        window = QObject()
        plugin._ready(window)
        window.deleteLater()
        QCoreApplication.sendPostedEvents(None, QEvent.Type.DeferredDelete)
        self.application.processEvents()
        self.assertTrue(plugin._closed)
        self.assertFalse(plugin._started)

    def test_actual_request_is_bounded_and_cancelled_without_restart(self):
        plugin = self.plugin()
        plugin._window = QObject()
        plugin._installed = "4.2.1"
        plugin._start()
        reply = plugin._reply
        manager = plugin._manager
        timer = plugin._timer
        self.assertEqual(reply.url().toString(), RELEASE_URL)
        self.assertEqual(reply.readBufferSize(), MAX_BYTES + 1)
        self.assertEqual(
            reply.request().attribute(QNetworkRequest.Attribute.RedirectPolicyAttribute),
            QNetworkRequest.RedirectPolicy.SameOriginRedirectPolicy,
        )
        self.assertEqual(timer.interval(), 12000)
        self.assertTrue(timer.isSingleShot())
        self.assertEqual(manager.parent(), plugin._window)
        plugin._start()
        self.assertIs(plugin._reply, reply)
        plugin._close()
        self.assertTrue(reply.isFinished())
        self.assertFalse(timer.isActive())
        self.assertIsNone(plugin._reply)
        self.assertIsNone(plugin._manager)
        self.assertIsNone(plugin._timer)
        self.assertEqual(plugin._data, b"")
        self.assertIsNone(plugin._dialog)
        plugin._start()
        self.assertIsNone(plugin._reply)

    def run_response(self, body, status=200, delay=0, timeout=None, disable_at=None):
        server = ThreadingHTTPServer(("127.0.0.1", 0), ResponseHandler)
        server.body = body
        server.status = status
        server.delay = delay
        thread = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.01}, daemon=True)
        thread.start()
        try:
            plugin = self.plugin()
            plugin._window = QWidget()
            plugin._installed = "4.2.1"
            loop = QEventLoop()
            deadline = QTimer()
            deadline.setSingleShot(True)
            deadline.timeout.connect(loop.quit)
            with patch.object(notice, "RELEASE_URL", f"http://127.0.0.1:{server.server_port}/releases"):
                plugin._start()
                plugin._reply.finished.connect(loop.quit)
                if timeout is not None:
                    plugin._timer.start(timeout)
                if disable_at is not None:
                    QTimer.singleShot(disable_at, self.organizer.disable)
                deadline.start(2000)
                loop.exec()
            deadline.stop()
            self.assertIsNone(plugin._reply)
            self.assertIsNone(plugin._timer)
            self.assertIsNone(plugin._manager)
            self.assertEqual(plugin._data, b"")
            self.assertEqual(plugin._started, disable_at is None)
            return plugin
        finally:
            server.shutdown()
            server.server_close()
            thread.join()

    def release_body(self, version):
        return json.dumps([{
            "game": "cyberpunk2077",
            "version": version,
            "force_down": False,
            "links": {"machineURL": "SchoolBoyQ"},
        }]).encode()

    def test_actual_async_get_reports_newer_release(self):
        plugin = self.run_response(self.release_body("4.2.2"))
        self.assertIsNotNone(plugin._dialog)
        self.assertEqual(plugin._dialog.text(), "Chrome & Blood 4.2.2 is available. Open Wabbajack to install the update.")
        self.assertEqual({button.text() for button in plugin._dialog.buttons()}, {"Open Wabbajack", "Close", "Don't show this version again"})

    def test_matching_release_is_silent(self):
        self.assertIsNone(self.run_response(self.release_body("4.2.1"))._dialog)

    def test_http_error_and_malformed_body_are_silent(self):
        self.assertIsNone(self.run_response(self.release_body("4.2.2"), status=503)._dialog)
        self.assertIsNone(self.run_response(b"{")._dialog)

    def test_body_limit_aborts_real_reply(self):
        self.assertIsNone(self.run_response(b" " * (MAX_BYTES + 1))._dialog)

    def test_deadline_aborts_real_pending_reply(self):
        self.assertIsNone(self.run_response(self.release_body("4.2.2"), delay=0.1, timeout=20)._dialog)

    def test_disabled_plugin_does_not_read_metadata_or_start_network(self):
        self.organizer.enabled = False
        with patch.object(notice, "installed_version") as read_metadata:
            plugin = self.plugin()
            self.organizer.ready(QObject())
            self.application.processEvents()
            self.assertFalse(plugin._started)
            self.assertIsNone(plugin._reply)
            read_metadata.assert_not_called()

    def test_disabling_cancels_actual_pending_reply(self):
        plugin = self.run_response(self.release_body("4.2.2"), delay=0.1, disable_at=20)
        self.assertFalse(plugin._started)
        self.assertIsNone(plugin._dialog)

    def test_disable_clears_notice_and_reenable_reads_current_baseline(self):
        plugin = self.run_response(self.release_body("4.2.2"))
        self.organizer.disable()
        self.assertIsNone(plugin._dialog)
        with patch.object(notice, "installed_version", return_value=None) as read_metadata:
            self.organizer.enable()
            self.application.processEvents()
            read_metadata.assert_called_once_with(self.organizer.directory)
        self.assertIsNone(plugin._reply)

    def notification(self, version="4.2.2"):
        plugin = self.plugin()
        plugin._window = QWidget()
        plugin._notice = Notice("4.2.1", version)
        plugin._notify()
        return plugin

    def click(self, plugin, text):
        button = next(button for button in plugin._dialog.buttons() if button.text() == text)
        button.click()
        self.assertIsNone(plugin._dialog)

    def test_close_does_not_persist_and_only_notifies_once_per_session(self):
        plugin = self.notification()
        self.click(plugin, "Close")
        self.assertEqual(self.organizer.preferences, {})
        plugin._notify()
        self.assertIsNone(plugin._dialog)
        restarted = self.notification()
        self.assertIsNotNone(restarted._dialog)

    def test_dialog_rejection_does_not_persist(self):
        plugin = self.notification()
        plugin._dialog.reject()
        self.assertIsNone(plugin._dialog)
        self.assertEqual(self.organizer.preferences, {})

    def test_skip_persists_only_that_numeric_version(self):
        plugin = self.notification()
        self.click(plugin, "Don't show this version again")
        self.assertEqual(self.organizer.preferences, {(plugin.name(), "ignored_version"): "4.2.2.0"})
        same = self.notification("4.2.2.0")
        self.assertIsNone(same._dialog)
        newer = self.notification("4.2.3")
        self.assertIsNotNone(newer._dialog)

    def test_preview_uses_installed_version_and_skip_does_not_persist(self):
        plugin = self.plugin()
        plugin._window = QWidget()
        plugin.display()
        self.assertIn("preview", plugin._dialog.windowTitle())
        self.assertIn("4.2.1", plugin._dialog.text())
        self.assertIn("will not be saved", plugin._dialog.informativeText())
        self.click(plugin, "Don't show this version again")
        self.assertEqual(self.organizer.preferences, {})
        self.assertFalse(plugin._shown)

    def test_open_button_closes_then_opens_fixed_wabbajack_url(self):
        plugin = self.notification()
        with patch.object(notice.QDesktopServices, "openUrl", return_value=True) as open_url:
            self.click(plugin, "Open Wabbajack")
            self.assertEqual(open_url.call_args.args[0], QUrl(notice.WABBAJACK_URL))
            open_url.assert_called_once()
        self.assertEqual(self.organizer.preferences, {})

    def test_open_falls_back_to_fixed_collection_page(self):
        plugin = self.notification()
        with patch.object(notice.QDesktopServices, "openUrl", side_effect=[False, True]) as open_url:
            self.click(plugin, "Open Wabbajack")
            self.assertEqual([call.args[0] for call in open_url.call_args_list], [QUrl(notice.WABBAJACK_URL), QUrl(notice.COLLECTION_URL)])

    def test_disabled_plugin_closes_dialog_and_cannot_preview_or_persist(self):
        plugin = self.notification()
        self.organizer.disable()
        self.assertIsNone(plugin._dialog)
        plugin.display()
        self.assertIsNone(plugin._dialog)
        self.assertEqual(self.organizer.preferences, {})

    def test_dialog_factory_keeps_only_one_live_dialog(self):
        plugin = self.notification()
        dialog = plugin._dialog
        with patch.object(dialog, "raise_") as raise_dialog, patch.object(dialog, "activateWindow") as activate:
            plugin.display()
            self.assertIs(plugin._dialog, dialog)
            raise_dialog.assert_called_once()
            activate.assert_called_once()

    def test_real_update_waits_for_preview_to_close(self):
        plugin = self.plugin()
        plugin._window = QWidget()
        plugin.display()
        preview = plugin._dialog
        plugin._notice = Notice("4.2.1", "4.2.2")
        plugin._notify()
        self.assertIs(plugin._dialog, preview)
        self.assertFalse(plugin._shown)
        self.click(plugin, "Close")
        self.application.processEvents()
        self.assertIn("4.2.2", plugin._dialog.text())
        self.assertTrue(plugin._shown)


if __name__ == "__main__":
    unittest.main()
