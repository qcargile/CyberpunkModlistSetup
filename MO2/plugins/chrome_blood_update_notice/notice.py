import mobase
from PyQt6.QtCore import QCoreApplication, QTimer, QUrl, Qt
from PyQt6.QtGui import QDesktopServices, QIcon
from PyQt6.QtNetwork import QNetworkAccessManager, QNetworkReply, QNetworkRequest
from PyQt6.QtWidgets import QMessageBox

from .release import COLLECTION_URL, MAX_BYTES, RELEASE_URL, WABBAJACK_URL, installed_version, update_notice, version_parts


class ChromeBloodUpdateNotice(mobase.IPluginTool):
    def __init__(self):
        super().__init__()
        self._organizer = None
        self._window = None
        self._installed = None
        self._notice = None
        self._dialog = None
        self._shown = False
        self._manager = None
        self._reply = None
        self._timer = None
        self._data = bytearray()
        self._started = False
        self._closed = False

    def name(self):
        return "Chrome & Blood Release Notice"

    def author(self):
        return "Chrome & Blood"

    def description(self):
        return "Reports available Chrome & Blood updates in Mod Organizer."

    def version(self):
        return mobase.VersionInfo(1, 0, 0)

    def settings(self):
        return []

    def displayName(self):
        return "Preview Chrome & Blood update notice"

    def tooltip(self):
        return "Preview the Chrome & Blood update notification."

    def icon(self):
        return QIcon()

    def display(self):
        if self._closed or not self._active() or self._window is None:
            return
        version = self._notice.available if self._notice is not None else installed_version(self._organizer.basePath())
        if version is not None:
            self._show_notification(version, preview=True)

    def init(self, organizer):
        self._organizer = organizer
        organizer.onPluginDisabled(self.name(), self._disabled)
        organizer.onPluginEnabled(self.name(), self._enabled)
        organizer.onUserInterfaceInitialized(self._ready)
        application = QCoreApplication.instance()
        if application is not None:
            application.aboutToQuit.connect(self._close)
        return True

    def _open_wabbajack(self):
        if QDesktopServices.openUrl(QUrl(WABBAJACK_URL)):
            return
        if not QDesktopServices.openUrl(QUrl(COLLECTION_URL)):
            QMessageBox.warning(
                self._window,
                "Chrome & Blood update",
                "Could not open Wabbajack or your browser. Open the Chrome & Blood Nexus collection to update:\n"
                + COLLECTION_URL,
            )

    def _notify(self):
        if self._closed or not self._active() or self._shown or self._notice is None or self._dialog is not None:
            return
        ignored = self._organizer.persistent(self.name(), "ignored_version", "")
        try:
            if version_parts(ignored) == version_parts(self._notice.available):
                return
        except ValueError:
            pass
        self._shown = True
        self._show_notification(self._notice.available)

    def _show_notification(self, version, preview=False):
        if self._closed or not self._active() or self._window is None:
            return
        if self._dialog is not None:
            self._dialog.raise_()
            self._dialog.activateWindow()
            return
        dialog = QMessageBox(self._window)
        dialog.setWindowTitle("Chrome & Blood update notice preview" if preview else "Chrome & Blood update")
        dialog.setIcon(QMessageBox.Icon.Information)
        dialog.setTextFormat(Qt.TextFormat.PlainText)
        dialog.setText(f"Chrome & Blood {version} is available. Open Wabbajack to install the update.")
        if preview:
            dialog.setInformativeText("Preview only. Dismissal preferences will not be saved.")
        else:
            dialog.setInformativeText("Close the game and Mod Organizer before starting the installation in Wabbajack.")
        open_button = dialog.addButton("Open Wabbajack", QMessageBox.ButtonRole.AcceptRole)
        close_button = dialog.addButton("Close", QMessageBox.ButtonRole.RejectRole)
        skip_button = dialog.addButton("Don't show this version again", QMessageBox.ButtonRole.ActionRole)
        dialog.setDefaultButton(close_button)
        dialog.setEscapeButton(close_button)
        dialog.setWindowModality(Qt.WindowModality.NonModal)
        self._dialog = dialog
        dialog.finished.connect(lambda _: self._notification_finished(dialog, open_button, skip_button, version, preview))
        dialog.show()

    def _notification_finished(self, dialog, open_button, skip_button, version, preview):
        if self._dialog is not dialog:
            return
        self._dialog = None
        clicked = dialog.clickedButton()
        dialog.deleteLater()
        if self._closed or not self._active():
            return
        if clicked is open_button:
            self._open_wabbajack()
        elif clicked is skip_button and not preview:
            canonical = ".".join(str(part) for part in version_parts(version))
            self._organizer.setPersistent(self.name(), "ignored_version", canonical)
        if preview:
            QTimer.singleShot(0, self._notify)

    def _close_dialog(self):
        dialog = self._dialog
        self._dialog = None
        if dialog is not None:
            dialog.close()
            dialog.deleteLater()

    def _ready(self, window):
        if self._closed or self._started:
            return
        self._window = window
        window.destroyed.connect(self._close)
        QTimer.singleShot(0, self._start)

    def _start(self):
        if self._closed or self._started or self._window is None or not self._active():
            return
        self._started = True
        self._installed = installed_version(self._organizer.basePath())
        if self._installed is None:
            return
        manager = QNetworkAccessManager(self._window)
        request = QNetworkRequest(QUrl(RELEASE_URL))
        request.setAttribute(
            QNetworkRequest.Attribute.RedirectPolicyAttribute,
            QNetworkRequest.RedirectPolicy.SameOriginRedirectPolicy,
        )
        request.setRawHeader(b"User-Agent", b"Chrome-Blood-MO2-Release-Notice/1.0.0")
        request.setRawHeader(b"Accept", b"application/json")
        request.setTransferTimeout(12000)
        self._manager = manager
        reply = manager.get(request)
        self._reply = reply
        reply.setReadBufferSize(MAX_BYTES + 1)
        reply.readyRead.connect(self._read)
        reply.finished.connect(self._finished)
        timer = QTimer(self._window)
        timer.setSingleShot(True)
        timer.timeout.connect(self._release)
        self._timer = timer
        timer.start(12000)

    def _read(self):
        reply = self._reply
        if self._closed or reply is None:
            return
        data = reply.read(MAX_BYTES + 1 - len(self._data))
        if data:
            self._data.extend(data)
        if len(self._data) > MAX_BYTES:
            self._release()

    def _finished(self):
        reply = self._reply
        if self._closed or reply is None:
            return
        if not self._active():
            self._disabled()
            return
        self._read()
        if self._reply is None:
            return
        valid = (
            reply.error() == QNetworkReply.NetworkError.NoError
            and reply.attribute(QNetworkRequest.Attribute.HttpStatusCodeAttribute) == 200
        )
        data = bytes(self._data) if valid else b""
        self._release()
        if valid:
            self._notice = update_notice(data, self._installed)
            if self._notice is not None:
                self._notify()

    def _close(self, *_):
        self._closed = True
        self._release()
        self._close_dialog()

    def _active(self):
        if self._organizer is None:
            return False
        game = self._organizer.managedGame()
        return game is not None and game.gameShortName() == "cyberpunk2077" and self._organizer.isPluginEnabled(self.name())

    def _disabled(self):
        self._notice = None
        self._installed = None
        self._started = False
        self._release()
        self._close_dialog()

    def _enabled(self):
        if not self._closed and self._window is not None:
            QTimer.singleShot(0, self._start)

    def _release(self):
        reply, timer, manager = self._reply, self._timer, self._manager
        self._reply = None
        self._timer = None
        self._manager = None
        self._data.clear()
        if timer is not None:
            timer.stop()
            timer.deleteLater()
        if reply is not None:
            if not reply.isFinished():
                reply.abort()
            reply.deleteLater()
        if manager is not None:
            manager.deleteLater()
