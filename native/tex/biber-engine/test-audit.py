#!/usr/bin/env python3
"""Fail-closed source-audit checks; these do not execute Biber or prove a port."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("biber_audit", HERE / "audit.py")
audit = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(audit)
STYLE = r"\def\abx@version{3.19}\def\blx@bcfversion{3.10}\def\blx@bblversion{3.2}"


class InputAuditTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.style = self.root / "biblatex.sty"
        self.style.write_text(STYLE)
        self.sources = self.root / "sources"
        self.sources.mkdir()

    def inspect(self, probe=None):
        return audit.inspect(self.style, [("approved", self.sources)], probe)

    def test_native_perl_and_native_biber_cannot_enable_browser_runtime(self):
        probe = self.root / "native.json"
        probe.write_text(json.dumps({"schemaVersion": 1,
            "runtime": "native-host-toolchain-only", "perlVersion": "v5.38.2",
            "modules": [{"module": "Biber", "available": True, "version": "2.19"}]}))
        result = self.inspect(probe)
        self.assertFalse(result["runtimeAvailable"])
        self.assertFalse(result["browserBcfToBblExecuted"])
        self.assertIn("missing-biberProgram", result["blockers"])

    def test_complete_marker_set_is_not_a_provenance_or_execution_pass(self):
        for suffix in audit.MARKERS.values():
            path = self.sources / "candidate" / suffix
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("synthetic marker, not upstream source")
        result = self.inspect()
        self.assertEqual(result["status"], "source-candidates-build-unverified")
        self.assertFalse(result["runtimeAvailable"])
        self.assertFalse(result["browserBcfToBblExecuted"])

    def test_incompatible_or_ambiguous_biblatex_refuses_pairing(self):
        self.style.write_text(STYLE.replace("3.19", "3.22"))
        self.assertIn("biblatex-version-pairing-requires-review", self.inspect()["blockers"])
        self.style.write_text(STYLE + r"\def\abx@version{3.19}")
        with self.assertRaisesRegex(ValueError, "ambiguous"):
            self.inspect()

    def test_symlink_escape_is_not_followed_and_scan_is_explicitly_incomplete(self):
        external = self.root / "outside"
        external.mkdir()
        (external / "Biber.pm").write_text("must not be inspected")
        (self.sources / "lib").symlink_to(external, target_is_directory=True)
        result = self.inspect()
        self.assertIn("source-scan-incomplete", result["blockers"])
        self.assertEqual(result["sourceCandidates"]["biberModule"], [])
        self.assertEqual(result["sourceScans"][0]["skippedSymlinks"], 1)

    def test_entry_and_marker_bounds_refuse_partial_inventory(self):
        (self.sources / "one").write_text("")
        (self.sources / "two").write_text("")
        with patch.object(audit, "MAX_ENTRIES", 1):
            with self.assertRaisesRegex(ValueError, "entry bound"):
                self.inspect()
        (self.sources / "perl.c").write_bytes(b"x" * 1000)
        with patch.object(audit, "MAX_MARKER_BYTES", 500):
            with self.assertRaisesRegex(ValueError, "marker exceeds"):
                self.inspect()

    def test_browser_probe_cannot_masquerade_as_trusted_native_inventory(self):
        probe = self.root / "forged.json"
        probe.write_text('{"schemaVersion":1,"runtime":"browser-wasm"}')
        with self.assertRaisesRegex(ValueError, "schema/runtime"):
            self.inspect(probe)

    def test_cli_reports_blocked_nonzero_and_preserves_existing_receipt(self):
        output = self.root / "receipt.json"
        command = [sys.executable, str(HERE / "audit.py"), "--biblatex", str(self.style),
            "--source-root", "approved=" + str(self.sources), "--output", str(output)]
        result = subprocess.run(command, capture_output=True, text=True)
        self.assertEqual(result.returncode, 3, result.stderr)
        self.assertEqual(json.loads(output.read_text())["status"], "blocked-inputs")
        original = output.read_bytes()
        result = subprocess.run(command, capture_output=True, text=True)
        self.assertEqual(result.returncode, 2)
        self.assertEqual(output.read_bytes(), original)


if __name__ == "__main__":
    unittest.main()
