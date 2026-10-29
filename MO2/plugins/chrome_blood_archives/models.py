import json

from PyQt6.QtCore import QAbstractTableModel, QByteArray, QMimeData, QModelIndex, Qt, pyqtSignal
from PyQt6.QtGui import QColor, QBrush, QIcon


class ArchiveModel(QAbstractTableModel):
    orderChanged = pyqtSignal()
    MIME = "application/x-chrome-blood-archive"
    HEADERS = ("Order", "Archive", "Mod", "Current wins", "Current losses", "Same", "Unknown", "Relationship", "Conflicts")
    CONFLICT_COLUMN = 8

    def __init__(self, parent=None):
        super().__init__(parent)
        self.rows = []
        self.editable_order = []
        self.relationships = {}
        self.writable = False
        self.session = ""
        self.conflict_icons = {name: QIcon(":/MO/gui/emblem_conflict_" + resource) for name, resource in (("Mixed", "mixed"), ("Wins", "overwrite"), ("Loses", "overwritten"), ("Identical", "redundant"))}

    def reset(self, rows, editable_order, session):
        self.beginResetModel()
        by_name = {row["name"]: row for row in rows}
        self.rows = [by_name[name] for name in editable_order if name in by_name]
        self.rows.extend(row for row in rows if not row["editable"])
        self.editable_order = list(editable_order)
        self.relationships = {}
        self.session = session
        self.endResetModel()

    def rowCount(self, parent=QModelIndex()):
        return 0 if parent.isValid() else len(self.rows)

    def columnCount(self, parent=QModelIndex()):
        return len(self.HEADERS)

    def headerData(self, section, orientation, role=Qt.ItemDataRole.DisplayRole):
        if role == Qt.ItemDataRole.DisplayRole and orientation == Qt.Orientation.Horizontal:
            return self.HEADERS[section]
        if role == Qt.ItemDataRole.ToolTipRole and orientation == Qt.Orientation.Horizontal and section in (3, 4, 5, 6):
            return "Counts describe the current effective order. Selection colors and the winner preview reflect the proposed order."
        if role == Qt.ItemDataRole.ToolTipRole and orientation == Qt.Orientation.Horizontal:
            return {0: "First archive wins. Drag to change priority; headers never sort the order.", 1: "Archive filename. Ctrl/Shift selects multiple archives.", 2: "The enabled mod supplying this archive.", self.CONFLICT_COLUMN: "Current resource conflicts across all active archives. Row colors show relationships with your selection."}.get(section)
        return None

    def data(self, index, role=Qt.ItemDataRole.DisplayRole):
        if not index.isValid() or index.row() >= len(self.rows):
            return None
        row = self.rows[index.row()]
        relation = self.relationships.get(row["name"], {})
        if index.column() == self.CONFLICT_COLUMN:
            conflict = self.conflict_label(row)
            icon = self.conflict_icons.get(conflict)
            if role == Qt.ItemDataRole.DecorationRole and icon is not None and not icon.isNull():
                return icon
            if role == Qt.ItemDataRole.AccessibleTextRole:
                return conflict
            if role == Qt.ItemDataRole.DisplayRole:
                return "" if icon is not None and not icon.isNull() else conflict
        if role in (Qt.ItemDataRole.DisplayRole, Qt.ItemDataRole.AccessibleTextRole):
            values = (str(index.row() + 1) if row["editable"] else "REDmod", row["name"], row["provider"], row["winning"], row["losing"], row["same"], row["unknown"], relation.get("tone", ""))
            return values[index.column()]
        if role == Qt.ItemDataRole.BackgroundRole and relation.get("backgroundHex"):
            color = QColor(relation["backgroundHex"])
            return QBrush(color) if color.isValid() else None
        if role == Qt.ItemDataRole.ForegroundRole and relation.get("colorHex"):
            color = QColor(relation["colorHex"])
            return QBrush(color) if color.isValid() else None
        if role == Qt.ItemDataRole.ToolTipRole:
            meaning = {"Unknown": "Some resource winners could not be determined.", "Mixed": "Some shared resources win and others lose.", "Wins": "This archive wins overlapping resources.", "Loses": "Other archives win overlapping resources.", "Identical": "Some shared resources are identical; the archive may still contain unique resources.", "": "No overlapping resources were found."}[self.conflict_label(row)]
            return f"{row['name']}\nMod: {row['provider']}\n{meaning}\nCurrent overlaps: {row['winning']} wins, {row['losing']} losses, {row['same']} identical, {row['unknown']} unknown.\n" + row["physicalPath"] + ("\nREDmod order is read-only." if not row["editable"] else "\nFirst archive wins.") + ("\nSelected relationship: " + relation["tone"] if relation.get("tone") else "")
        return None

    def conflict_label(self, row):
        if row["unknown"]:
            return "Unknown"
        if row["winning"] and row["losing"]:
            return "Mixed"
        if row["winning"]:
            return "Wins"
        if row["losing"]:
            return "Loses"
        return "Identical" if row["same"] else ""

    def set_relationships(self, relationships):
        updated = {row["archiveName"]: row for row in relationships}
        if updated == self.relationships:
            return
        self.relationships = updated
        if self.rows:
            self.dataChanged.emit(self.index(0, 0), self.index(len(self.rows) - 1, len(self.HEADERS) - 1),
                                  [Qt.ItemDataRole.ForegroundRole, Qt.ItemDataRole.BackgroundRole, Qt.ItemDataRole.ToolTipRole])
            self.dataChanged.emit(self.index(0, 7), self.index(len(self.rows) - 1, 7),
                                  [Qt.ItemDataRole.DisplayRole, Qt.ItemDataRole.AccessibleTextRole])

    def flags(self, index):
        if not index.isValid():
            return Qt.ItemFlag.ItemIsDropEnabled if self.writable else Qt.ItemFlag.NoItemFlags
        flags = Qt.ItemFlag.ItemIsEnabled | Qt.ItemFlag.ItemIsSelectable
        if self.writable and self.rows[index.row()]["editable"]:
            flags |= Qt.ItemFlag.ItemIsDragEnabled | Qt.ItemFlag.ItemIsDropEnabled
        return flags

    def mimeTypes(self):
        return [self.MIME]

    def mimeData(self, indexes):
        mime = QMimeData()
        rows = sorted({index.row() for index in indexes})
        if self.writable and rows and all(self.rows[row]["editable"] for row in rows):
            payload = {"session": self.session, "archives": [self.rows[row]["name"] for row in rows]}
            mime.setData(self.MIME, QByteArray(json.dumps(payload).encode("utf-8")))
        return mime

    def supportedDropActions(self):
        return Qt.DropAction.MoveAction

    def canDropMimeData(self, data, action, row, column, parent):
        destination = parent.row() if parent.isValid() else row
        return self.writable and action == Qt.DropAction.MoveAction and data.hasFormat(self.MIME) and -1 <= destination <= len(self.editable_order)

    def dropMimeData(self, data, action, row, column, parent):
        if not self.canDropMimeData(data, action, row, column, parent):
            return False
        try:
            payload = json.loads(bytes(data.data(self.MIME)))
            names = payload["archives"]
            if payload["session"] != self.session or not isinstance(names, list) or not names or len(names) != len(set(names)) or any(name not in self.editable_order for name in names):
                return False
        except (ValueError, KeyError, TypeError):
            return False
        destination = parent.row() if parent.isValid() else row
        if destination < 0:
            destination = len(self.editable_order)
        original = self.editable_order
        selected = set(names)
        moved = [name for name in original if name in selected]
        before = sum(name not in selected for name in original[:destination])
        remaining = [name for name in original if name not in selected]
        order = remaining[:before] + moved + remaining[before:]
        if order == original:
            return False
        by_name = {item["name"]: item for item in self.rows}
        self.beginResetModel()
        self.editable_order = order
        self.rows = [by_name[name] for name in order] + [item for item in self.rows if not item["editable"]]
        self.relationships = {}
        self.endResetModel()
        self.orderChanged.emit()
        return True
