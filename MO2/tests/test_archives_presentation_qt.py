import json
import os
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "plugins"))

try:
    from PyQt6.QtCore import QCoreApplication, QEvent, QItemSelectionModel, Qt
    from PyQt6.QtGui import QColor
    from PyQt6.QtWidgets import QApplication, QTreeView
    from chrome_blood_archives.models import ArchiveModel
    from chrome_blood_archives.views import ArchiveList
except ImportError:
    ArchiveModel = None


def archive(name, wins=0, losses=0, same=0, unknown=0, editable=True):
    return {"name": name, "provider": "Mod " + name, "physicalPath": "C:/mods/" + name, "position": 0, "editable": editable, "winning": wins, "losing": losses, "same": same, "unknown": unknown, "unique": 4}


@unittest.skipIf(ArchiveModel is None, "Requires MO2's native Python 3.12 and Qt libraries")
class ArchivePresentationTests(unittest.TestCase):
    def test_unchanged_relationships_do_not_invalidate_the_view(self):
        model = ArchiveModel()
        model.reset([archive("a.archive")], ["a.archive"], "one")
        changes = []
        model.dataChanged.connect(lambda *_: changes.append(True))
        model.set_relationships([])
        self.assertEqual(changes, [])
        relationships = [{"archiveName": "a.archive", "tone": "Wins", "colorHex": "#42E58B", "backgroundHex": "#08251B"}]
        model.set_relationships(relationships)
        count = len(changes)
        self.assertGreater(count, 0)
        model.set_relationships(relationships)
        self.assertEqual(len(changes), count)
        self.assertEqual(model.data(model.index(0, 1), Qt.ItemDataRole.ForegroundRole).color(), QColor("#42E58B"))
        model.set_relationships([])
        self.assertIsNone(model.data(model.index(0, 1), Qt.ItemDataRole.ForegroundRole))

    @classmethod
    def setUpClass(cls):
        cls.application = QApplication.instance() or QApplication([])
        cls.application.setQuitOnLastWindowClosed(False)

    def setUp(self):
        self.model = ArchiveModel()
        self.model.reset([archive("a.archive", wins=2), archive("b.archive", losses=3), archive("c.archive", wins=1, losses=2), archive("REDmod/d.archive", same=3, editable=False)], ["a.archive", "b.archive", "c.archive"], "scan-one")
        self.model.writable = True
        self.view = ArchiveList(self.model)
        self.view.setAttribute(Qt.WidgetAttribute.WA_DontShowOnScreen, True)
        self.view.resize(760, 320)
        self.slot_errors = []
        original = sys.excepthook
        sys.excepthook = lambda kind, error, trace: self.slot_errors.append((kind, error))
        self.addCleanup(setattr, sys, "excepthook", original)

    def tearDown(self):
        self.view.close()
        self.view.deleteLater()
        QCoreApplication.sendPostedEvents(None, QEvent.Type.DeferredDelete)
        self.application.processEvents()
        self.assertEqual(self.slot_errors, [])

    def select(self, names):
        selected = set(names)
        selection = self.view.selectionModel()
        selection.clearSelection()
        for row, value in enumerate(self.model.rows):
            if value["name"] in selected:
                selection.select(self.model.index(row, 1), QItemSelectionModel.SelectionFlag.Select | QItemSelectionModel.SelectionFlag.Rows)

    def test_four_native_columns_keep_model_priority_and_name_api(self):
        self.assertIsInstance(self.view, QTreeView)
        header = self.view.header()
        visible = [header.logicalIndex(position) for position in range(header.count()) if not self.view.isColumnHidden(header.logicalIndex(position))]
        self.assertEqual(visible, [0, 1, self.model.CONFLICT_COLUMN, 2])
        self.assertEqual([self.model.headerData(column, Qt.Orientation.Horizontal) for column in visible], ["Order", "Archive", "Conflicts", "Mod"])
        self.assertFalse(self.view.isSortingEnabled())
        self.assertFalse(header.sectionsClickable())
        self.assertEqual(self.model.data(self.model.index(0, 1)), "a.archive")
        self.assertIn("First archive wins", self.model.headerData(0, Qt.Orientation.Horizontal, Qt.ItemDataRole.ToolTipRole))

    def test_global_conflicts_and_unknown_are_independent_of_selection_colors(self):
        column = self.model.CONFLICT_COLUMN
        labels = [self.model.data(self.model.index(row, column), Qt.ItemDataRole.AccessibleTextRole) for row in range(4)]
        self.assertEqual(labels, ["Wins", "Loses", "Mixed", "Identical"])
        self.model.rows[0]["unknown"] = 1
        self.assertEqual(self.model.data(self.model.index(0, column), Qt.ItemDataRole.AccessibleTextRole), "Unknown")
        self.assertIn("could not be determined", self.model.data(self.model.index(0, column), Qt.ItemDataRole.ToolTipRole))
        self.model.set_relationships([{"archiveName": "b.archive", "tone": "Wins", "colorHex": "#42E58B", "backgroundHex": "#08251B"}])
        self.assertEqual(self.model.data(self.model.index(1, column), Qt.ItemDataRole.AccessibleTextRole), "Loses")
        self.assertEqual(self.model.data(self.model.index(1, 7), Qt.ItemDataRole.AccessibleTextRole), "Wins")
        for row in range(4):
            index = self.model.index(row, column)
            self.assertTrue(self.model.data(index) or self.model.data(index, Qt.ItemDataRole.DecorationRole) is not None)

    def test_move_actions_use_existing_order_contract_and_keep_redmods_separate(self):
        self.select(["b.archive", "c.archive"])
        self.assertEqual(self.view.selected_names(), ["b.archive", "c.archive"])
        self.assertTrue(self.view.move_selected(False))
        self.assertEqual(self.model.editable_order, ["b.archive", "c.archive", "a.archive"])
        self.assertEqual(self.model.rows[-1]["name"], "REDmod/d.archive")
        self.select(["REDmod/d.archive"])
        self.assertFalse(self.view.move_selected(False))
        self.model.writable = False
        self.select(["a.archive"])
        self.assertFalse(self.view.move_selected(True))

    def test_native_theme_selection_and_core_relationship_strip_render(self):
        self.view.setStyleSheet("QTreeView::item:selected { background: #7A2455; color: #FFFFFF; }")
        self.model.set_relationships([{"archiveName": "b.archive", "tone": "Wins", "colorHex": "#42E58B", "backgroundHex": "#08251B"}])
        self.select(["a.archive"])
        self.view.show()
        self.application.processEvents()
        image = self.view.viewport().grab().toImage()
        ratio = image.devicePixelRatio()
        selected_rect = self.view.visualRect(self.model.index(0, 1))
        relation_rect = self.view.visualRect(self.model.index(1, 0))
        self.assertEqual(image.pixelColor(round((selected_rect.right() - 12) * ratio), round(selected_rect.center().y() * ratio)).name(), "#7a2455")
        self.assertEqual(image.pixelColor(round((relation_rect.left() + 1) * ratio), round(relation_rect.center().y() * ratio)), QColor("#42E58B"))

    def test_recorded_profile_renders_with_current_mo2_theme(self):
        baseline = os.environ.get("MO2_ARCHIVES_BASELINE_JSON")
        theme = os.environ.get("MO2_ARCHIVES_THEME_QSS")
        image_path = os.environ.get("MO2_ARCHIVES_PRESENTATION_IMAGE")
        if not baseline or not theme or not image_path:
            self.skipTest("Set recorded profile, theme and image output for presentation QA")
        data = json.loads(Path(baseline).read_text(encoding="utf-8-sig"))
        self.model.reset(data["archiveRows"], data["editableOrder"], data["token"])
        self.view.setStyleSheet(Path(theme).read_text(encoding="utf-8-sig"))
        self.view.resize(920, 650)
        first_conflict = next((row for row, value in enumerate(self.model.rows) if self.model.conflict_label(value)), 0)
        self.select([self.model.rows[first_conflict]["name"]])
        self.view.show()
        self.application.processEvents()
        self.view.scrollTo(self.model.index(first_conflict, 1), QTreeView.ScrollHint.PositionAtTop)
        self.assertEqual(self.model.rowCount(), len(data["archiveRows"]))
        self.assertGreater(self.view.verticalScrollBar().maximum(), 0)
        self.assertTrue(self.view.grab().save(image_path))


if __name__ == "__main__":
    unittest.main()
