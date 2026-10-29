import unittest

from test_archives_qt import Organizer, archive, mobase

if mobase is not None:
    import tempfile
    from PyQt6.QtCore import QCoreApplication, QEvent, QItemSelectionModel, QSortFilterProxyModel, Qt
    from PyQt6.QtGui import QStandardItem, QStandardItemModel
    from PyQt6.QtWidgets import QApplication, QMainWindow, QSplitter, QTabWidget, QTreeView, QWidget
    from chrome_blood_archives import createPlugin


@unittest.skipIf(mobase is None, "Requires MO2's native Python and Qt libraries")
class ArchiveModSelectionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.application = QApplication.instance() or QApplication([])
        cls.application.setQuitOnLastWindowClosed(False)

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.organizer = Organizer(self.directory.name)
        self.organizer.names = ["Owner A", "Owner B"]
        self.organizer.allMods = lambda: self.organizer.names
        self.organizer.activate_game()
        self.window = QMainWindow()
        splitter = QSplitter(self.window)
        self.mods = QTreeView(splitter)
        self.mods.setObjectName("modList")
        self.mod_model = QStandardItemModel(self.mods)
        for number, text in enumerate(("Display A", "Display B")):
            item = QStandardItem(text)
            item.setData(number, Qt.ItemDataRole.UserRole + 1)
            self.mod_model.appendRow(item)
        self.mods.setModel(self.mod_model)
        tabs = QTabWidget(splitter)
        tabs.setObjectName("tabWidget")
        for name in ("dataTab", "savesTab", "downloadTab"):
            widget = QWidget(tabs)
            widget.setObjectName(name)
            tabs.addTab(widget, name)
        self.window.setCentralWidget(splitter)
        self.plugin = createPlugin()
        self.plugin.init(self.organizer)
        self.plugin.ready(self.window)
        self.panel = self.plugin.panel
        self.panel.request_context = self.plugin.context()
        self.panel.operation_generation = self.panel.generation
        self.panel.needs_scan = False
        state = {"token": "one", "profilePath": str(self.organizer.profile_path / "modlist.txt"), "profileName": "First", "orderSource": None, "writeTarget": str(self.organizer.profile_path / "archive-order" / "modlist.txt"), "writeBlockedReason": None, "effectiveOrder": ["a.archive", "a2.archive", "b.archive"], "editableOrder": ["a.archive", "a2.archive", "b.archive"], "archiveRows": [{**archive("a.archive"), "provider": "Owner A"}, {**archive("a2.archive"), "provider": "Owner A"}, {**archive("b.archive"), "provider": "Owner B"}], "canUndo": False, "archiveFailures": [], "archiveWarnings": [], "orderMessage": "", "palette": []}
        self.plugin.result("scan", "scan", state)
        self.panel.timer.stop()

    def tearDown(self):
        self.organizer.enabled = False
        self.plugin.disabled()
        self.window.deleteLater()
        QCoreApplication.sendPostedEvents(None, QEvent.Type.DeferredDelete)
        self.application.processEvents()
        self.directory.cleanup()

    def test_archive_selection_selects_its_mod_without_selecting_every_archive_in_that_mod(self):
        self.panel.set_selected_archives(["a.archive"])

        self.assertEqual([index.row() for index in self.mods.selectionModel().selectedRows()], [0])
        self.assertEqual(self.panel.selection_names, ["a.archive"])
        self.assertFalse(self.plugin.syncing_selection)

        self.panel.set_selected_archives(["a.archive", "b.archive"])

        self.assertEqual({index.row() for index in self.mods.selectionModel().selectedRows()}, {0, 1})
        self.assertEqual(self.panel.selection_names, ["a.archive", "b.archive"])

    def test_mod_selection_selects_its_archives_and_keeps_the_mod_selection(self):
        self.mods.selectionModel().select(self.mod_model.index(0, 0), QItemSelectionModel.SelectionFlag.ClearAndSelect | QItemSelectionModel.SelectionFlag.Rows)

        self.assertEqual(self.panel.selection_names, ["a.archive", "a2.archive"])
        self.assertEqual([index.row() for index in self.mods.selectionModel().selectedRows()], [0])
        self.assertTrue(self.panel.pending_selection)

    def test_syncing_multiple_archives_emits_one_selection_update(self):
        updates = []
        self.panel.archives.selectionModel().selectionChanged.connect(lambda *_: updates.append(True))

        self.panel.set_selected_archives(["a.archive", "a2.archive", "b.archive"])

        self.assertEqual(self.panel.selection_names, ["a.archive", "a2.archive", "b.archive"])
        self.assertEqual(len(updates), 1)
        self.panel.set_selected_archives([])
        self.assertEqual(self.panel.selection_names, [])
        self.assertEqual(len(updates), 2)

    def test_distinct_native_mod_names_do_not_merge_during_selection(self):
        self.organizer.names = ["Straße", "Strasse"]
        for row in self.panel.archive_model.rows:
            row["provider"] = "Straße" if row["name"] in ("a.archive", "a2.archive") else "Strasse"
        self.mods.selectionModel().select(self.mod_model.index(0, 0), QItemSelectionModel.SelectionFlag.ClearAndSelect | QItemSelectionModel.SelectionFlag.Rows)
        self.assertEqual(self.panel.selection_names, ["a.archive", "a2.archive"])
        self.panel.set_selected_archives(["b.archive"])
        self.assertEqual([index.row() for index in self.mods.selectionModel().selectedRows()], [1])

    def test_replaced_grouped_model_rebinds_and_matches_internal_identity(self):
        replacement = QStandardItemModel(self.mods)
        group = QStandardItem("Group")
        item = QStandardItem("Another displayed name")
        item.setData(1, Qt.ItemDataRole.UserRole + 1)
        group.appendRow(item)
        replacement.appendRow(group)
        self.mods.setModel(replacement)
        self.application.processEvents()
        child = replacement.index(0, 0, replacement.index(0, 0))
        self.mods.selectionModel().select(child, QItemSelectionModel.SelectionFlag.ClearAndSelect | QItemSelectionModel.SelectionFlag.Rows)

        self.assertIs(self.plugin.mod_selection, self.mods.selectionModel())
        self.assertEqual(self.panel.selection_names, ["b.archive"])

        self.panel.set_selected_archives([])
        self.panel.set_selected_archives(["b.archive"])

        self.assertTrue(self.mods.isExpanded(replacement.index(0, 0)))
        self.assertEqual(self.mods.selectionModel().selectedRows(), [child])

    def test_a_filtered_owner_does_not_clear_unrelated_mod_selection_or_the_filter(self):
        proxy = QSortFilterProxyModel(self.mods)
        proxy.setSourceModel(self.mod_model)
        proxy.setFilterFixedString("Display A")
        original_filter = proxy.filterRegularExpression().pattern()
        self.mods.setModel(proxy)
        self.application.processEvents()
        self.mods.selectionModel().select(proxy.index(0, 0), QItemSelectionModel.SelectionFlag.ClearAndSelect | QItemSelectionModel.SelectionFlag.Rows)

        self.panel.set_selected_archives(["b.archive"])

        self.assertEqual([index.data() for index in self.mods.selectionModel().selectedRows()], ["Display A"])
        self.assertEqual(proxy.filterRegularExpression().pattern(), original_filter)
        self.assertIn("mod-list filter", self.panel.status.text())
