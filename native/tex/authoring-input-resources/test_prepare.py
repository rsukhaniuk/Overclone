"""Fixture-backed recipient recipe checks; no downloads or project inputs."""
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("resource_prepare", Path(__file__).with_name("prepare.py"))
recipe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(recipe)


@unittest.skipUnless(all(os.environ.get(k) for k in (
    "AUTHORING_ARCHIVES", "AUTHORING_PARENT", "AUTHORING_CATALOG_TOOL")), "Explicit retained resource fixtures required")
class ResourceRecipeTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory()
        self.addCleanup(self.scratch.cleanup)
        self.directory = Path(self.scratch.name)
        self.archives = Path(os.environ["AUTHORING_ARCHIVES"])
        self.parent = Path(os.environ["AUTHORING_PARENT"])
        self.tool = Path(os.environ["AUTHORING_CATALOG_TOOL"])
        self.lock = json.loads(Path(__file__).with_name("resources.lock.json").read_text())

    def test_exact_full_archive_and_true_font_catalogue_closure(self):
        out = self.directory / "prepared"
        result = recipe.prepare(self.archives, self.parent, self.tool, out)
        self.assertEqual(result["archivesVerified"], 29)
        self.assertEqual(result["resourceCount"], 105)
        self.assertEqual(result["closureSha256"], "842f5bc8b01071a50c8f2fca9d33a932ec504250971f873a5f456a295157e725")
        self.assertTrue(result["original77FontsByteExact"])
        self.assertEqual(result["fontAliases"], 0)
        catalogue = (out / "files/xetexfontlist.txt").read_bytes()
        self.assertTrue(catalogue.startswith((self.parent / "files/xetexfontlist.txt").read_bytes()))

    def test_modified_archive_refuses_before_output_creation(self):
        directory = self.directory / "archives"
        directory.mkdir()
        first = self.lock["archives"][0]["name"]
        data = bytearray((self.archives / first).read_bytes())
        data[-1] ^= 1
        (directory / first).write_bytes(data)
        out = self.directory / "prepared"
        with self.assertRaisesRegex(ValueError, "integrity mismatch"):
            recipe.prepare(directory, self.parent, self.tool, out)
        self.assertFalse(out.exists())

    def test_mixed_parent_refuses_before_output_creation(self):
        directory = self.directory / "parent"
        directory.mkdir()
        (directory / "pins.json").write_text("[]\n")
        out = self.directory / "prepared"
        with self.assertRaisesRegex(ValueError, "immutable P15"):
            recipe.prepare(self.archives, directory, self.tool, out)
        self.assertFalse(out.exists())

    def test_modified_metadata_extractor_refuses_before_execution(self):
        directory = self.directory / "tool"
        directory.mkdir()
        (directory / "font-catalog.js").write_text("throw Error('untrusted tool must never run');")
        out = self.directory / "prepared"
        with self.assertRaisesRegex(ValueError, "integrity mismatch"):
            recipe.prepare(self.archives, self.parent, directory / "font-catalog.js", out)
        self.assertFalse(out.exists())


if __name__ == "__main__":
    unittest.main()
