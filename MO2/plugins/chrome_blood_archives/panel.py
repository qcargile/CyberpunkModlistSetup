from PyQt6.QtCore import QItemSelection, QItemSelectionModel, QTimer, Qt, pyqtSignal
from PyQt6.QtWidgets import QCheckBox, QHBoxLayout, QLabel, QLineEdit, QProgressBar, QPushButton, QVBoxLayout, QWidget

from .models import ArchiveModel
from .views import ArchiveList


class ArchivesPanel(QWidget):
    archivesSelected = pyqtSignal(object)

    def __init__(self, owner, parent=None):
        super().__init__(parent)
        self.owner = owner
        self.state = None
        self.preview = None
        self.generation = 0
        self.request_context = None
        self.needs_scan = True
        self.pending_selection = False
        self.pending_preview = False
        self.selection_names = []
        self.restoring_selection = False
        self.reset_selection = ("", [])
        self.drafts = {}
        self.view_states = {}
        self.pending_order_conflict = False
        self.closed = False
        self.title = QLabel("Archives", self)
        self.refresh_button = QPushButton("Refresh", self)
        self.refresh_button.clicked.connect(self.invalidate)
        self.cancel_button = QPushButton("Cancel", self)
        self.cancel_button.clicked.connect(self.cancel)
        self.cancel_button.setEnabled(False)
        top = QHBoxLayout()
        top.addWidget(self.title, 1)
        top.addWidget(self.refresh_button)
        top.addWidget(self.cancel_button)
        self.status = QLabel("Select this tab to read the active profile's archives.", self)
        self.status.setWordWrap(True)
        self.status.setTextInteractionFlags(Qt.TextInteractionFlag.TextSelectableByMouse)
        self.progress_bar = QProgressBar(self)
        self.progress_bar.hide()
        self.archive_model = ArchiveModel(self)
        self.archive_model.orderChanged.connect(self.order_changed)
        self.archives = ArchiveList(self.archive_model, self)
        self.archives.selectionModel().selectionChanged.connect(self.selection_changed)
        self.archives.owningModRequested.connect(self.archivesSelected.emit)
        self.archives.resetOrderRequested.connect(lambda: self.reset_saved_order())
        self.archives.can_reset_order = lambda: self.state is not None and self.archive_model.editable_order != self.state["editableOrder"]
        self.archive_model.modelAboutToBeReset.connect(self.remember_selection)
        self.archive_model.modelReset.connect(self.restore_selection)
        self.search = QLineEdit(self)
        self.search.setPlaceholderText("Find an archive…")
        self.search.setClearButtonEnabled(True)
        self.search.setAccessibleName("Find an archive")
        self.search.setToolTip("Find an archive without changing or filtering load order.")
        self.search.textChanged.connect(self.find_archive)
        self.preview_label = QLabel(self)
        self.preview_label.setWordWrap(True)
        self.preview_label.hide()
        self.acknowledge = QCheckBox("I understand that unreadable archives or unresolved load order leave effects that cannot be previewed.", self)
        self.acknowledge.setVisible(False)
        self.acknowledge.toggled.connect(self.acknowledgement_changed)
        self.save_button = QPushButton("Save order", self)
        self.save_button.clicked.connect(self.save)
        self.undo_button = QPushButton("Undo last save", self)
        self.undo_button.clicked.connect(self.undo)
        self.save_button.setEnabled(False)
        self.undo_button.setEnabled(False)
        actions = QHBoxLayout()
        actions.addWidget(self.undo_button)
        actions.addWidget(self.save_button)
        layout = QVBoxLayout(self)
        layout.addLayout(top)
        layout.addWidget(self.status)
        layout.addWidget(self.progress_bar)
        layout.addWidget(self.search)
        layout.addWidget(self.archives, 1)
        layout.addWidget(self.preview_label)
        layout.addWidget(self.acknowledge)
        layout.addLayout(actions)
        self.timer = QTimer(self)
        self.timer.setSingleShot(True)
        self.timer.timeout.connect(self.dispatch)
        self.progress_timer = QTimer(self)
        self.progress_timer.setSingleShot(True)
        self.progress_timer.timeout.connect(self.show_progress)
        self.last_progress = None

    def visible(self):
        if not self.closed and (self.needs_scan or self.pending_selection or self.pending_preview):
            self.timer.start(0)

    def invalidate(self, *_, rescan=True):
        if self.closed:
            return
        self.preserve_draft()
        self.timer.stop()
        self.generation += 1
        self.needs_scan = True
        self.state = None
        self.preview = None
        self.pending_selection = False
        self.pending_preview = False
        self.archive_model.writable = False
        self.archive_model.reset([], [], "")
        self.selection_names = []
        self.acknowledge.setChecked(False)
        self.acknowledge.hide()
        self.save_button.setEnabled(False)
        self.undo_button.setEnabled(False)
        self.preview_label.hide()
        self.status.setText("Profile or mods changed. Refreshing archive order…")
        self.owner.bridge.cancel()
        if rescan and self.isVisible():
            self.timer.start(150)

    def has_draft(self):
        return self.state is not None and self.archive_model.editable_order != self.state["editableOrder"]

    def preserve_draft(self):
        if self.state is None:
            return
        path = self.state["profilePath"]
        self.view_states[path] = (list(self.selection_names), self.archives.verticalScrollBar().value())
        if self.has_draft():
            baseline = self.drafts[path]["baseline"] if self.pending_order_conflict and path in self.drafts else self.state["editableOrder"]
            self.drafts[path] = {"order": list(self.archive_model.editable_order), "baseline": list(baseline)}
        else:
            self.drafts.pop(path, None)

    def reset_saved_order(self):
        if self.state is None or self.owner.bridge.pending is not None and self.owner.bridge.pending["operation"] in ("apply", "undo"):
            return
        self.drafts.pop(self.state["profilePath"], None)
        self.pending_order_conflict = False
        self.archive_model.reset(self.state["archiveRows"], self.state["editableOrder"], self.state["token"])
        self.order_changed()
        self.update_actions()

    def restore_draft(self, data, operation):
        path = data["profilePath"]
        if operation in ("apply", "undo"):
            self.drafts.pop(path, None)
        self.pending_order_conflict = False
        draft = self.drafts.get(path)
        order = list(data["editableOrder"])
        if draft is not None:
            current = set(order)
            previous = set(draft["baseline"])
            self.pending_order_conflict = [name for name in order if name in previous] != [name for name in draft["baseline"] if name in current]
            kept = [name for name in draft["order"] if name in current]
            kept_names = set(kept)
            order = kept + [name for name in order if name not in kept_names]
            if order == data["editableOrder"]:
                self.drafts.pop(path, None)
                self.pending_order_conflict = False
        self.archive_model.reset(data["archiveRows"], order, data["token"])
        self.pending_preview = order != data["editableOrder"] or bool(data.get("needsOrderWrite"))
        if path in self.view_states:
            selected, position = self.view_states[path]
            self.set_selected_archives(selected)
            self.archives.verticalScrollBar().setValue(position)

    def dispatch(self):
        if self.closed or not self.isVisible() or not self.owner.active() or self.owner.bridge.pending is not None:
            return
        try:
            context = self.owner.context()
            if self.state is not None and context != self.request_context:
                self.invalidate()
                return
            if self.needs_scan:
                if not self.isVisible():
                    return
                self.request_context = context
                self.needs_scan = False
                self.start("scan", instanceRoot=context[0], profileModlistPath=context[1], activeProviders=[{"name": name, "priority": priority} for name, priority in context[2]])
            elif self.pending_preview and self.state is not None:
                self.pending_preview = False
                self.start("preview", token=self.state["token"], order=self.archive_model.editable_order, unknownImpactAcknowledged=self.acknowledge.isChecked())
            elif self.pending_selection and self.state is not None:
                self.pending_selection = False
                self.start("select", token=self.state["token"], selectedArchives=self.selection_names, order=self.archive_model.editable_order, offset=0, limit=1)
        except (OSError, RuntimeError, ValueError) as error:
            self.status.setText(str(error))
            self.needs_scan = True

    def start(self, operation, **arguments):
        self.operation_generation = self.generation
        self.owner.bridge.request(operation, **arguments)
        self.save_button.setEnabled(False)
        self.undo_button.setEnabled(False)
        self.cancel_button.setEnabled(True)
        self.archive_model.writable = operation == "select" and not self.state.get("writeBlockedReason")
        if operation in ("scan", "apply", "undo"):
            self.progress_bar.setRange(0, 0)
            self.progress_bar.show()
            self.status.setText({"scan": "Reading archives…", "apply": "Saving and verifying this profile's archive order…", "undo": "Restoring and verifying the previous archive order…"}[operation])

    def current_response(self):
        try:
            return self.operation_generation == self.generation and self.request_context == self.owner.context() and self.owner.active()
        except (OSError, RuntimeError, ValueError):
            return False

    def result(self, identifier, operation, data):
        if self.closed:
            return
        self.finish_operation()
        if not self.current_response():
            self.timer.start(0)
            return
        if operation in ("scan", "apply", "undo"):
            self.state = data
            self.preview = None
            self.set_color_tooltip(data.get("palette", []))
            self.title.setText("Archives · " + data["profileName"])
            self.restore_draft(data, operation)
            self.preview_label.hide()
            self.acknowledge.setChecked(False)
            self.acknowledge.hide()
            prefix = "Saved and verified. " if operation == "apply" else "Previous order restored and verified. " if operation == "undo" else ""
            errors = data.get("archiveFailures", [])
            warnings = data.get("archiveWarnings", [])
            message = f"{prefix}{len(data['archiveRows'])} archives. First archive wins."
            if data.get("writeBlockedReason"):
                message += " " + data["writeBlockedReason"]
            elif errors or warnings:
                message += f" {len(errors)} unreadable, {len(warnings)} warnings. Open Conflict Studio to inspect them."
            if self.has_draft():
                message += " Unsaved order kept."
            if self.pending_order_conflict:
                message += " The saved order also changed; Save will ask which order to use."
            self.status.setText(message)
            self.owner.watch_state(data)
            if operation in ("apply", "undo"):
                self.owner.refresh_mappings()
        elif operation == "select":
            if not self.pending_selection and not self.pending_preview:
                self.archive_model.set_relationships(data["relationships"])
        elif operation == "preview":
            self.preview = data
            message = "Ready to save this order for the active profile." if not self.has_draft() else f"{len(data['changedArchives'])} archives move; {data['totalWinnerDeltas']} resource winners change." if data["winnerChangesKnown"] else f"{len(data['changedArchives'])} archives move. Winner changes cannot be fully determined from the current order."
            self.preview_label.setText(message + (" " + data["blockedReason"] if data.get("blockedReason") else ""))
            self.acknowledge.setVisible(data["unknownImpact"] == "RequiresAcknowledgement")
        self.update_actions()
        self.timer.start(0)

    def failed(self, identifier, operation, message, cancelled):
        if self.closed:
            return
        self.finish_operation()
        if self.current_response():
            self.status.setText("Cancelled. Refresh to read the current profile." if cancelled else message)
            if operation in ("scan", "apply", "undo"):
                self.preserve_draft()
                self.state = None
                self.archive_model.writable = False
                self.save_button.setEnabled(False)
                self.undo_button.setEnabled(False)
            else:
                self.preview = None
                self.update_actions()
        self.timer.start(0)

    def finish_operation(self):
        self.progress_timer.stop()
        self.last_progress = None
        self.progress_bar.hide()
        self.cancel_button.setEnabled(False)

    def update_actions(self):
        idle = self.owner.bridge.pending is None
        valid = self.state is not None and self.owner.active()
        self.archive_model.writable = bool(idle and valid and not self.state.get("writeBlockedReason"))
        changed = valid and self.archive_model.editable_order != self.state["editableOrder"]
        needs_write = valid and (changed or self.state.get("needsOrderWrite", False))
        self.preview_label.setVisible(bool(needs_write))
        self.save_button.setEnabled(bool(idle and needs_write and self.preview is not None and self.preview["canApply"] and not self.pending_preview))
        self.undo_button.setEnabled(bool(idle and valid and self.state["canUndo"] and not changed))

    def progress(self, identifier, data):
        if self.closed or self.operation_generation != self.generation:
            return
        self.last_progress = data
        if not self.progress_timer.isActive():
            self.progress_timer.start(80)

    def show_progress(self):
        data = self.last_progress
        if data is None:
            return
        total = data.get("total") or 0
        self.progress_bar.setRange(0, max(0, total))
        self.progress_bar.setValue(data.get("completed") or 0)
        self.status.setText(str(data.get("phase", "Reading archives")))

    def selection_changed(self, *_):
        if self.state is None or self.restoring_selection:
            return
        rows = sorted(index.row() for index in self.archives.selectionModel().selectedRows(1))
        self.selection_names = [self.archive_model.rows[row]["name"] for row in rows]
        self.archive_model.set_relationships([])
        self.pending_selection = True
        self.archivesSelected.emit(self.selection_names)
        self.timer.start(120)

    def set_selected_archives(self, names):
        selected = set(names)
        self.restoring_selection = True
        selection = self.archives.selectionModel()
        replacement = QItemSelection()
        first = None
        for row, archive in enumerate(self.archive_model.rows):
            if archive["name"] in selected:
                index = self.archive_model.index(row, 1)
                replacement.select(index, index)
                if first is None:
                    first = index
        selection.select(replacement, QItemSelectionModel.SelectionFlag.ClearAndSelect | QItemSelectionModel.SelectionFlag.Rows)
        if first is not None:
            selection.setCurrentIndex(first, QItemSelectionModel.SelectionFlag.NoUpdate)
            self.archives.scrollTo(first)
        self.restoring_selection = False
        self.selection_changed()

    def remember_selection(self):
        self.reset_selection = (self.archive_model.session, list(self.selection_names))
        self.restoring_selection = True

    def restore_selection(self):
        session, names = self.reset_selection
        self.restoring_selection = False
        self.set_selected_archives(names if session == self.archive_model.session else [])

    def find_archive(self, query):
        query = query.strip().casefold()
        if query:
            match = next((row["name"] for row in self.archive_model.rows if query in row["name"].casefold()), None)
            if match is not None:
                self.set_selected_archives([match])

    def order_changed(self):
        self.preview = None
        self.acknowledge.setChecked(False)
        self.acknowledge.hide()
        self.pending_preview = True
        self.pending_selection = True
        self.preview_label.setText("Checking proposed winner changes…")
        self.preview_label.show()
        self.save_button.setEnabled(False)
        self.timer.start(120)

    def acknowledgement_changed(self, *_):
        if self.state is not None and self.preview is not None:
            self.preview = None
            self.pending_preview = True
            self.save_button.setEnabled(False)
            self.timer.start(0)

    def save(self):
        if not self.save_button.isEnabled() or not self.current_response():
            return
        if self.pending_order_conflict:
            decision = (self.generation, self.state["token"], tuple(self.archive_model.editable_order))
            choice = self.owner.confirm_draft()
            if self.state is None or self.owner.bridge.pending is not None or not self.current_response() or decision != (self.generation, self.state["token"], tuple(self.archive_model.editable_order)):
                self.status.setText("The order changed again. Review the refreshed order before saving.")
                return
            if choice == "saved":
                self.reset_saved_order()
                return
            if choice != "draft":
                return
            self.pending_order_conflict = False
        blocked = self.owner.write_blocked()
        if blocked:
            self.status.setText(blocked)
            self.save_button.setEnabled(False)
            return
        self.preserve_draft()
        self.start("apply", token=self.state["token"], order=self.archive_model.editable_order)

    def undo(self):
        if not self.undo_button.isEnabled() or not self.current_response():
            return
        blocked = self.owner.write_blocked()
        if blocked:
            self.status.setText(blocked)
            self.undo_button.setEnabled(False)
            return
        self.start("undo", token=self.state["token"])

    def cancel(self):
        self.owner.bridge.cancel()
        self.status.setText("Cancelling; waiting for the archive engine to confirm the result…")
        self.cancel_button.setEnabled(False)

    def set_color_tooltip(self, relationships):
        labels = {"Wins": "Selected wins", "Loses": "Selected loses", "Mixed": "Mixed", "Same": "Identical", "Unknown": "Unknown"}
        parts = [f'<span style="background-color:{row["backgroundHex"]};color:{row["colorHex"]}"> {labels.get(row["tone"], row["tone"])} </span>' for row in relationships if row.get("colorHex") and row.get("backgroundHex")]
        self.archives.setToolTip("Higher archives win. Ctrl/Shift selects multiple archives; drag to reorder. REDmod order is separate.<br>" + " · ".join(parts))

    def shutdown(self):
        self.closed = True
        self.timer.stop()
        self.progress_timer.stop()
        self.setEnabled(False)
