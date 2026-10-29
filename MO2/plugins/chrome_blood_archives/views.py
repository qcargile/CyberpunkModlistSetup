from PyQt6.QtCore import QModelIndex, QSize, Qt, pyqtSignal
from PyQt6.QtGui import QPainter, QPalette
from PyQt6.QtWidgets import QApplication, QHeaderView, QMenu, QScrollBar, QStyle, QStyledItemDelegate, QStyleOptionSlider, QStyleOptionViewItem, QTreeView


class ArchiveDelegate(QStyledItemDelegate):
    def sizeHint(self, option, index):
        size = super().sizeHint(option, index)
        return QSize(size.width(), max(22, size.height()))

    def paint(self, painter, option, index):
        styled = QStyleOptionViewItem(option)
        self.initStyleOption(styled, index)
        styled.palette.setColor(QPalette.ColorRole.Text, option.palette.color(QPalette.ColorRole.Text))
        style = option.widget.style() if option.widget is not None else QApplication.style()
        style.drawControl(QStyle.ControlElement.CE_ItemViewItem, styled, painter, option.widget)
        stripe = index.data(Qt.ItemDataRole.ForegroundRole)
        if index.column() == 0 and stripe is not None:
            painter.save()
            painter.fillRect(option.rect.x(), option.rect.y(), 3, option.rect.height(), stripe)
            painter.restore()


class ArchiveList(QTreeView):
    owningModRequested = pyqtSignal(object)
    resetOrderRequested = pyqtSignal()

    def __init__(self, model, parent=None):
        super().__init__(parent)
        self.setModel(model)
        self.can_reset_order = lambda: False
        self.setRootIsDecorated(False)
        self.setItemsExpandable(False)
        self.setUniformRowHeights(True)
        self.setAllColumnsShowFocus(True)
        self.setSelectionBehavior(QTreeView.SelectionBehavior.SelectRows)
        self.setSortingEnabled(False)
        self.setWordWrap(False)
        self.setTextElideMode(Qt.TextElideMode.ElideRight)
        self.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.setVerticalScrollMode(QTreeView.ScrollMode.ScrollPerPixel)
        self.setSelectionMode(QTreeView.SelectionMode.ExtendedSelection)
        self.setEditTriggers(QTreeView.EditTrigger.NoEditTriggers)
        self.setDragDropMode(QTreeView.DragDropMode.InternalMove)
        self.setDefaultDropAction(Qt.DropAction.MoveAction)
        self.setDragDropOverwriteMode(False)
        self.setDropIndicatorShown(True)
        self.setAutoScroll(True)
        self.setAutoScrollMargin(32)
        self.setItemDelegate(ArchiveDelegate(self))
        self.setVerticalScrollBar(ArchiveScrollBar(self))
        header = self.header()
        header.setSectionsClickable(False)
        header.setSectionsMovable(False)
        header.setMinimumSectionSize(28)
        header.setStretchLastSection(False)
        header.setSectionResizeMode(QHeaderView.ResizeMode.Interactive)
        for column in range(model.columnCount()):
            if column not in (0, 1, 2, model.CONFLICT_COLUMN):
                self.hideColumn(column)
        header.moveSection(header.visualIndex(model.CONFLICT_COLUMN), 2)
        header.setSectionResizeMode(1, QHeaderView.ResizeMode.Stretch)
        self.setColumnWidth(0, 50)
        self.setColumnWidth(model.CONFLICT_COLUMN, 72)
        self.setColumnWidth(2, 150)
        self.setContextMenuPolicy(Qt.ContextMenuPolicy.CustomContextMenu)
        self.customContextMenuRequested.connect(self.context_menu)
        self.setAccessibleName("Archive load order")
        self.setToolTip("First archive wins. Ctrl/Shift selects multiple archives; drag to reorder. REDmod order is separate.")

    def selected_names(self):
        rows = sorted({index.row() for index in self.selectionModel().selectedIndexes()})
        return [self.model().rows[row]["name"] for row in rows]

    def move_selected(self, to_bottom):
        model = self.model()
        mime = model.mimeData(self.selectionModel().selectedIndexes())
        return model.dropMimeData(mime, Qt.DropAction.MoveAction, len(model.editable_order) if to_bottom else 0, 0, QModelIndex())

    def context_menu(self, position):
        index = self.indexAt(position)
        if not index.isValid():
            return
        if not self.selectionModel().isSelected(index):
            self.setCurrentIndex(index)
        names = self.selected_names()
        if not names:
            return
        menu = QMenu(self)
        select_mod = menu.addAction("Select owning mod")
        select_mod.triggered.connect(lambda: self.owningModRequested.emit(names))
        menu.addSeparator()
        editable = self.model().writable and all(name in self.model().editable_order for name in names)
        top = menu.addAction("Move to top")
        top.setEnabled(editable)
        top.triggered.connect(lambda: self.move_selected(False))
        bottom = menu.addAction("Move to bottom")
        bottom.setEnabled(editable)
        bottom.triggered.connect(lambda: self.move_selected(True))
        reset = menu.addAction("Reset unsaved order")
        reset.setEnabled(self.model().writable and self.can_reset_order())
        reset.triggered.connect(self.resetOrderRequested.emit)
        menu.addSeparator()
        copy = menu.addAction("Copy archive names" if len(names) > 1 else "Copy archive name")
        copy.triggered.connect(lambda: QApplication.clipboard().setText("\n".join(names)))
        menu.exec(self.viewport().mapToGlobal(position))
        menu.deleteLater()


class ArchiveScrollBar(QScrollBar):
    def __init__(self, view):
        super().__init__(Qt.Orientation.Vertical, view)
        self.view = view
        self.setAccessibleName("Archive relationship scrollbar markers")
        view.model().dataChanged.connect(self.update)
        view.model().modelReset.connect(self.update)
        view.selectionModel().selectionChanged.connect(self.update)

    def paintEvent(self, event):
        super().paintEvent(event)
        painter = QPainter(self)
        option = QStyleOptionSlider()
        self.initStyleOption(option)
        groove = self.style().subControlRect(QStyle.ComplexControl.CC_ScrollBar, option, QStyle.SubControl.SC_ScrollBarGroove, self)
        slider = self.style().subControlRect(QStyle.ComplexControl.CC_ScrollBar, option, QStyle.SubControl.SC_ScrollBarSlider, self)
        model = self.view.model()
        count = model.rowCount()
        if not count:
            return
        selected = {index.row() for index in self.view.selectionModel().selectedIndexes()}
        for row in range(count):
            stripe = model.data(model.index(row, 1), Qt.ItemDataRole.ForegroundRole)
            if stripe is not None:
                y = groove.top() + int(row * groove.height() / count)
                painter.fillRect(slider.left() + 2, y, max(1, slider.width() - 5), 3, stripe)
        for row in selected:
            y = groove.top() + int(row * groove.height() / count)
            painter.fillRect(slider.left() + 2, y, max(1, slider.width() - 5), 3, self.view.palette().highlight())
