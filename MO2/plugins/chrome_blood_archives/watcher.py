import ctypes
import os
import struct
import threading
from ctypes import wintypes
from pathlib import Path

from PyQt6.QtCore import QEventLoop, QObject, Qt, pyqtSignal, pyqtSlot


def path_key(name):
    return os.path.normcase(os.path.abspath(name))


class Overlapped(ctypes.Structure):
    _fields_ = [("Internal", ctypes.c_size_t), ("InternalHigh", ctypes.c_size_t), ("Offset", wintypes.DWORD), ("OffsetHigh", wintypes.DWORD), ("hEvent", wintypes.HANDLE)]


class DirectoryApi:
    def __init__(self):
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        signatures = {
            "CreateFileW": (wintypes.HANDLE, [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]),
            "CreateEventW": (wintypes.HANDLE, [wintypes.LPVOID, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR]),
            "ReadDirectoryChangesW": (wintypes.BOOL, [wintypes.HANDLE, wintypes.LPVOID, wintypes.DWORD, wintypes.BOOL, wintypes.DWORD, wintypes.LPVOID, ctypes.POINTER(Overlapped), wintypes.LPVOID]),
            "GetOverlappedResult": (wintypes.BOOL, [wintypes.HANDLE, ctypes.POINTER(Overlapped), ctypes.POINTER(wintypes.DWORD), wintypes.BOOL]),
            "CancelIoEx": (wintypes.BOOL, [wintypes.HANDLE, ctypes.POINTER(Overlapped)]),
            "WaitForMultipleObjects": (wintypes.DWORD, [wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE), wintypes.BOOL, wintypes.DWORD]),
            "SetEvent": (wintypes.BOOL, [wintypes.HANDLE]),
            "ResetEvent": (wintypes.BOOL, [wintypes.HANDLE]),
            "CloseHandle": (wintypes.BOOL, [wintypes.HANDLE]),
        }
        for name, (result, arguments) in signatures.items():
            function = getattr(self.kernel, name)
            function.restype = result
            function.argtypes = arguments


class DirectoryRead:
    def __init__(self, api, root, recursive):
        self.api = api
        self.root = root
        self.recursive = recursive
        self.handle = None
        self.event = None
        self.pending = False
        self.buffer = (wintypes.DWORD * 16384)()
        self.overlapped = Overlapped()
        native_path = root if root.startswith("\\\\?\\") else "\\\\?\\UNC\\" + root[2:] if root.startswith("\\\\") else "\\\\?\\" + root
        self.handle = api.CreateFileW(native_path, 0x0001, 0x0001 | 0x0002 | 0x0004, None, 3, 0x02000000 | 0x40000000, None)
        if self.handle == wintypes.HANDLE(-1).value:
            self.handle = None
            raise ctypes.WinError(ctypes.get_last_error())
        self.event = api.CreateEventW(None, True, False, None)
        if not self.event:
            error = ctypes.get_last_error()
            api.CloseHandle(self.handle)
            self.handle = None
            raise ctypes.WinError(error)
        self.overlapped.hEvent = self.event

    def start(self):
        self.api.ResetEvent(self.event)
        if not self.api.ReadDirectoryChangesW(self.handle, self.buffer, ctypes.sizeof(self.buffer), self.recursive, 0x0001 | 0x0002 | 0x0008 | 0x0010, None, ctypes.byref(self.overlapped), None):
            error = ctypes.get_last_error()
            if error != 997:
                raise ctypes.WinError(error)
        self.pending = True

    def complete(self):
        count = wintypes.DWORD()
        completed = self.api.GetOverlappedResult(self.handle, ctypes.byref(self.overlapped), ctypes.byref(count), False)
        self.pending = False
        if not completed:
            raise ctypes.WinError(ctypes.get_last_error())
        if not count.value:
            raise OSError("Too many directory changes arrived to track safely.")
        data = ctypes.string_at(self.buffer, count.value)
        changes = []
        offset = 0
        while offset < len(data):
            following, action, length = struct.unpack_from("<III", data, offset)
            name = data[offset + 12:offset + 12 + length].decode("utf-16-le", "surrogatepass")
            changes.append((path_key(os.path.join(self.root, name)), action))
            if not following:
                break
            offset += following
        self.start()
        return changes

    def close(self):
        if self.handle is not None:
            if self.pending:
                self.api.CancelIoEx(self.handle, ctypes.byref(self.overlapped))
                count = wintypes.DWORD()
                self.api.GetOverlappedResult(self.handle, ctypes.byref(self.overlapped), ctypes.byref(count), True)
                self.pending = False
            self.api.CloseHandle(self.handle)
            self.handle = None
        if self.event is not None:
            self.api.CloseHandle(self.event)
            self.event = None


class DirectoryWatcher(QObject):
    changed = pyqtSignal(object)
    failed = pyqtSignal(str)
    _changes = pyqtSignal(int, object)
    _failure = pyqtSignal(int, str)
    _armed = pyqtSignal(int)
    _finished = pyqtSignal(int)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.api = DirectoryApi().kernel
        self.thread = None
        self.stop_event = None
        self.completion = None
        self.generation = 0
        self.specification = None
        self.ready = False
        self._changes.connect(self.deliver, Qt.ConnectionType.QueuedConnection)
        self._failure.connect(self.report_failure, Qt.ConnectionType.QueuedConnection)
        self._armed.connect(self.armed, Qt.ConnectionType.QueuedConnection)

    def running(self):
        return self.thread is not None and self.thread.is_alive()

    def configure(self, roots, files, directories, ancestors, force=False):
        roots = tuple(sorted(set(roots)))
        specification = (roots, frozenset(files), frozenset(directories), frozenset(ancestors))
        if not force and specification == self.specification and self.running():
            return
        generation = self.stop()
        if generation != self.generation:
            return
        self.specification = specification
        if len(roots) > 63:
            self._failure.emit(generation, "Too many separate linked archive locations to monitor safely.")
            return
        if not roots:
            return
        self.stop_event = self.api.CreateEventW(None, True, False, None)
        if not self.stop_event:
            self.stop_event = None
            self._failure.emit(generation, str(ctypes.WinError(ctypes.get_last_error())))
            return
        self.completion = threading.Event()
        self.thread = threading.Thread(target=self.run, args=(generation, specification, self.stop_event, self.completion), name="ChromeBloodArchiveWatch")
        self.thread.start()

    def stop(self):
        self.generation += 1
        generation = self.generation
        thread = self.thread
        stop_event = self.stop_event
        completion = self.completion
        self.thread = None
        self.stop_event = None
        self.completion = None
        self.specification = None
        self.ready = False
        if thread is not None:
            if thread.is_alive():
                loop = QEventLoop()
                completed_generation = generation - 1
                def drained(finished_generation):
                    if finished_generation == completed_generation:
                        loop.quit()
                self._finished.connect(drained, Qt.ConnectionType.QueuedConnection)
                self.api.SetEvent(stop_event)
                if not completion.is_set():
                    loop.exec(QEventLoop.ProcessEventsFlag.ExcludeUserInputEvents)
                self._finished.disconnect(drained)
            thread.join()
        if stop_event is not None:
            self.api.CloseHandle(stop_event)
        return generation

    def run(self, generation, specification, stop_event, completion):
        roots, files, directories, ancestors = specification
        reads = []
        try:
            for root, recursive in roots:
                read = DirectoryRead(self.api, root, recursive)
                reads.append(read)
                read.start()
            self._armed.emit(generation)
            handles = (wintypes.HANDLE * (len(reads) + 1))(stop_event, *(read.event for read in reads))
            while True:
                result = self.api.WaitForMultipleObjects(len(handles), handles, False, 0xFFFFFFFF)
                if result == 0:
                    break
                if result == 0xFFFFFFFF:
                    raise ctypes.WinError(ctypes.get_last_error())
                changes = reads[result - 1].complete()
                relevant = tuple((name, action) for name, action in changes if name in files or Path(name).suffix.casefold() == ".archive" and str(Path(name).parent) in directories or action != 3 and name in ancestors)
                if relevant:
                    self._changes.emit(generation, relevant)
        except OSError as error:
            self._failure.emit(generation, str(error))
        finally:
            for read in reads:
                read.close()
            completion.set()
            self._finished.emit(generation)

    @pyqtSlot(int)
    def armed(self, generation):
        if generation == self.generation:
            self.ready = True

    @pyqtSlot(int, object)
    def deliver(self, generation, changes):
        if generation == self.generation:
            self.changed.emit(changes)

    @pyqtSlot(int, str)
    def report_failure(self, generation, message):
        if generation == self.generation:
            self.ready = False
            self.failed.emit(message)
