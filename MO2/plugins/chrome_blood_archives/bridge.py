import json
import uuid

from PyQt6.QtCore import QObject, QProcess, pyqtSignal


MAX_LINE_BYTES = 32 * 1024 * 1024


class ArchiveBridge(QObject):
    result = pyqtSignal(str, str, object)
    failed = pyqtSignal(str, str, str, bool)
    progress = pyqtSignal(str, object)
    stopped = pyqtSignal()

    def __init__(self, executable, parent=None):
        super().__init__(parent)
        self.executable = str(executable)
        self.process = QProcess(self)
        self.process.setProcessChannelMode(QProcess.ProcessChannelMode.SeparateChannels)
        self.process.started.connect(self._started)
        self.process.readyReadStandardOutput.connect(self._read)
        self.process.readyReadStandardError.connect(self._read_error)
        self.process.errorOccurred.connect(self._process_error)
        self.process.finished.connect(self._finished)
        self.buffer = bytearray()
        self.line_search_from = 0
        self.stderr = bytearray()
        self.pending = None
        self.cancel_requested = False
        self.closing = False

    def request(self, operation, **arguments):
        if self.closing or self.pending is not None:
            raise RuntimeError("Another archive operation is still finishing.")
        identifier = uuid.uuid4().hex
        message = {"id": identifier, "operation": operation, **arguments}
        self.pending = message
        self.cancel_requested = False
        if self.process.state() == QProcess.ProcessState.NotRunning:
            self.process.start(self.executable, [])
        else:
            self._write(message)
        return identifier

    def cancel(self):
        if self.pending is None or self.cancel_requested:
            return
        self.cancel_requested = True
        if self.process.state() == QProcess.ProcessState.Running:
            self._write({"id": uuid.uuid4().hex, "operation": "cancel"})

    def close(self):
        self.closing = True
        self.cancel()
        if self.process.state() == QProcess.ProcessState.Running:
            self.process.closeWriteChannel()
        elif self.process.state() == QProcess.ProcessState.NotRunning:
            self.stopped.emit()

    def _started(self):
        if self.pending is not None:
            self._write(self.pending)
            if self.cancel_requested:
                self._write({"id": uuid.uuid4().hex, "operation": "cancel"})
        if self.closing:
            self.process.closeWriteChannel()

    def _write(self, message):
        self.process.write(json.dumps(message, separators=(",", ":")).encode("utf-8") + b"\n")

    def _read(self):
        while self.process.bytesAvailable():
            chunk = bytes(self.process.read(min(65536, MAX_LINE_BYTES + 1 - len(self.buffer))))
            if not chunk:
                break
            self.feed(chunk)

    def feed(self, data):
        self.buffer.extend(data)
        while True:
            end = self.buffer.find(b"\n", self.line_search_from)
            if end < 0:
                self.line_search_from = len(self.buffer)
                break
            line = self.buffer[:end]
            del self.buffer[:end + 1]
            self.line_search_from = 0
            if len(line) > MAX_LINE_BYTES:
                self._protocol_error("The archive engine response exceeded the size limit. Refresh to reconnect.")
                return
            try:
                message = json.loads(line)
                if not isinstance(message, dict):
                    raise ValueError("Expected a response object")
                identifier = message["id"]
                kind = message["type"]
            except (ValueError, KeyError, UnicodeError):
                self._protocol_error("The archive engine returned an invalid response. Refresh to reconnect.")
                return
            pending = self.pending
            if pending is None or identifier != pending["id"]:
                continue
            operation = pending["operation"]
            if kind == "progress":
                self.progress.emit(identifier, message)
            elif kind == "result" and message.get("ok") is True and "data" in message:
                self.pending = None
                self.result.emit(identifier, operation, message["data"])
            elif kind == "error":
                self.pending = None
                self.failed.emit(identifier, operation, str(message.get("message", "Archive operation failed.")), bool(message.get("cancelled")))
            else:
                self._protocol_error("The archive engine returned an unsupported response. Refresh to reconnect.")
                return
        if len(self.buffer) > MAX_LINE_BYTES:
            self._protocol_error("The archive engine response exceeded the size limit. Refresh to reconnect.")

    def _protocol_error(self, message):
        pending = self.pending
        self.pending = None
        self.buffer.clear()
        self.line_search_from = 0
        self.closing = True
        self.process.closeWriteChannel()
        if pending is not None:
            self.failed.emit(pending["id"], pending["operation"], message, False)

    def _read_error(self):
        self.stderr.extend(bytes(self.process.readAllStandardError()))
        if len(self.stderr) > 8192:
            del self.stderr[:-8192]

    def _process_error(self, error):
        if error == QProcess.ProcessError.FailedToStart:
            pending = self.pending
            self.pending = None
            if pending is not None:
                self.failed.emit(pending["id"], pending["operation"], "Could not start Conflict Studio's archive engine. Restore Tools/Conflict Studio/Mo2Bridge and Refresh. " + self.process.errorString(), False)
            self.stopped.emit()

    def _finished(self, exit_code, exit_status):
        self._read()
        pending = self.pending
        self.pending = None
        self.buffer.clear()
        self.line_search_from = 0
        if pending is not None:
            message = "The archive engine stopped before confirming the result. Refresh to read the current profile order."
            if self.stderr:
                message += " " + self.stderr.decode("utf-8", errors="replace").strip()
            self.failed.emit(pending["id"], pending["operation"], message, False)
        self.stopped.emit()
