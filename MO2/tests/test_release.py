import json
import sys
import tempfile
import unittest
from pathlib import Path


sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "plugins"))

from chrome_blood_update_notice.release import MAX_BYTES, SETTINGS_NAME, Notice, installed_version, update_notice, version_parts


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.settings = {
            "ModListName": "Chrome & Blood",
            "MachineUrl": "Chrome_and_Blood/SchoolBoyQ",
            "Version": "4.2.1",
            "ModlistVersion": "0.0.1.0",
        }
        self.release = {
            "game": "cyberpunk2077",
            "version": "4.2.2",
            "force_down": False,
            "links": {
                "machineURL": "SchoolBoyQ",
                "nexusCollection": {
                    "slug": "lrcdrr",
                    "domainName": "cyberpunk2077",
                    "collectionId": "464419",
                    "lastRevisionNumber": 5,
                },
            },
        }

    def write_settings(self):
        (self.root / SETTINGS_NAME).write_text(json.dumps(self.settings), encoding="utf-8")

    def notice(self, installed="4.2.1"):
        return update_notice(json.dumps([self.release]).encode(), installed)

    def test_reads_current_settings_and_ignores_obsolete_version(self):
        self.write_settings()
        self.assertEqual(installed_version(self.root), "4.2.1")
        del self.settings["Version"]
        self.write_settings()
        self.assertIsNone(installed_version(self.root))

    def test_missing_or_wrong_settings_do_not_supply_baseline(self):
        self.assertIsNone(installed_version(self.root))
        for key, value in (("ModListName", "Other list"), ("MachineUrl", "Other/SchoolBoyQ")):
            with self.subTest(key=key):
                original = self.settings[key]
                self.settings[key] = value
                self.write_settings()
                self.assertIsNone(installed_version(self.root))
                self.settings[key] = original

    def test_rejects_invalid_local_json(self):
        for data in (
            b'{"Version":"4.2.1","Version":"4.2.2"}',
            b"[]", b"{", b"\xff", b"[" * 2000, b" " * (MAX_BYTES + 1),
        ):
            with self.subTest(data=data[:40]):
                (self.root / SETTINGS_NAME).write_bytes(data)
                self.assertIsNone(installed_version(self.root))

    def test_only_newer_numeric_version_produces_notice(self):
        for available, installed, expected in (
            ("4.2.2", "4.2.1", True),
            ("4.2.1", "4.2.1", False),
            ("4.2.0", "4.2.1", False),
            ("4.2.1.0", "4.2.1", False),
            ("4.2.1", "4.2.1.0", False),
            ("1.10", "1.9", True),
            ("1.9", "1.10", False),
        ):
            with self.subTest(available=available, installed=installed):
                self.release["version"] = available
                self.assertEqual(self.notice(installed), Notice(installed, available) if expected else None)

    def test_invalid_versions_are_rejected(self):
        for version in (None, True, 4.2, "4", "4.2.1.0.1", "4.-1", "4.2147483648", "4.2<script>", "4.2\n", "４.２"):
            with self.subTest(version=version):
                with self.assertRaises(ValueError):
                    version_parts(version)
                self.release["version"] = version
                self.assertIsNone(self.notice())
                self.assertIsNone(update_notice(b"[]", version))

    def test_wrong_or_ambiguous_remote_list_is_ignored(self):
        for key, value in (("game", "skyrimse"), ("links", {}), ("links", None)):
            with self.subTest(key=key, value=value):
                release = dict(self.release, **{key: value})
                self.assertIsNone(update_notice(json.dumps([release]).encode(), "4.2.1"))
        self.assertIsNone(update_notice(json.dumps([self.release, self.release]).encode(), "4.2.1"))
        self.release["links"]["machineURL"] = "AnotherList"
        self.assertIsNone(self.notice())

    def test_withdrawn_or_invalid_availability_is_ignored(self):
        for value in (True, None, "false", 0):
            with self.subTest(value=value):
                self.release["force_down"] = value
                self.assertIsNone(self.notice())

    def test_collection_revision_cannot_replace_installed_version(self):
        self.release["version"] = "4.2.1"
        self.release["links"]["nexusCollection"]["lastRevisionNumber"] = 100
        self.assertIsNone(self.notice())

    def test_malformed_collection_metadata_is_ignored(self):
        collection = self.release["links"]["nexusCollection"]
        for revision in (None, 0, -1, True, "6", 2147483648):
            with self.subTest(revision=revision):
                collection["lastRevisionNumber"] = revision
                self.assertIsNone(self.notice())
        self.release["links"]["nexusCollection"] = []
        self.assertIsNone(self.notice())

    def test_wabbajack_descriptor_does_not_require_nexus_metadata(self):
        del self.release["links"]["nexusCollection"]
        self.assertEqual(self.notice(), Notice("4.2.1", "4.2.2"))

    def test_invalid_remote_json_is_ignored(self):
        for data in (
            b"{}", b"null", b"[", b"\xff", b"[NaN]", b"[" * 2000,
            b" " * (MAX_BYTES + 1), b'[{"game":"cyberpunk2077","game":"skyrimse"}]',
        ):
            with self.subTest(data=data[:40]):
                self.assertIsNone(update_notice(data, "4.2.1"))


if __name__ == "__main__":
    unittest.main()
