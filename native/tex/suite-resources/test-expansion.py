#!/usr/bin/env python3
"""Packaging trust checks using the actual, unchanged original suite fixture."""
import json
import os
from pathlib import Path
import tempfile
import unittest

import expand


@unittest.skipUnless(os.environ.get('COMPILER_SUITE_RUNTIME_DIR'), 'Exact original four-engine suite fixture required')
class ExpansionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.base = Path(os.environ['COMPILER_SUITE_RUNTIME_DIR'])
        self.overlay = self.root / 'overlay'
        (self.overlay / 'files').mkdir(parents=True)
        self.source = b'% Synthetic packaging input, not a native compiler result.\n'
        self.pin = {'name': 'compatibility-test.sty', 'length': len(self.source), 'sha256': expand.digest(self.source)}
        (self.overlay / 'files' / self.pin['name']).write_bytes(self.source)
        self.write_manifest([self.pin])
        self.old = json.loads((self.base / 'pins.json').read_text())
        self.lock = {'schema': 1, 'baseClosureSha256': expand.BASE_CLOSURE,
                     'files': sorted(self.old + [self.pin], key=lambda p: p['name']), 'replacements': {}}

    def write_manifest(self, pins):
        (self.overlay / 'manifest.json').write_text(json.dumps({'schema': 1, 'files': pins}))

    def test_exact_addition_preserves_every_original_pin(self):
        pins, _ = expand.plan(self.base, [self.overlay], self.lock)
        self.assertEqual(len(self.old) + 1, len(pins))
        selected = {p['name']: p for p in pins}
        for pin in self.old:
            self.assertEqual(pin, selected[pin['name']])

    def test_changed_bytes_fail_even_when_manifest_and_lock_agree(self):
        (self.overlay / 'files' / self.pin['name']).write_bytes(b'x' * len(self.source))
        with self.assertRaisesRegex(ValueError, 'hash/length mismatch'):
            expand.plan(self.base, [self.overlay], self.lock)

    def test_overlay_cannot_replace_executable_even_with_a_matching_lock(self):
        original = next(p for p in self.old if p['name'] == 'engine.wasm')
        (self.overlay / 'files' / original['name']).write_bytes((self.base / 'files' / original['name']).read_bytes())
        self.write_manifest([original])
        self.lock['files'] = self.old
        with self.assertRaisesRegex(ValueError, 'executable components'):
            expand.plan(self.base, [self.overlay], self.lock)

    def test_runtime_data_cannot_be_replaced_by_an_overlay(self):
        name = 'babel.sty'
        changed = {**self.pin, 'name': name}
        (self.overlay / 'files' / name).write_bytes(self.source)
        self.write_manifest([changed])
        with self.assertRaisesRegex(ValueError, 'unapproved or conflicting replacement'):
            expand.plan(self.base, [self.overlay], self.lock)

    def test_explicit_format_replacement_requires_the_matching_before_and_after(self):
        before = next(p for p in self.old if p['name'] == 'pdflatex.fmt')
        after = {**self.pin, 'name': before['name']}
        (self.overlay / 'files' / after['name']).write_bytes(self.source)
        self.write_manifest([after])
        self.lock['files'] = sorted([p for p in self.old if p['name'] != after['name']] + [after], key=lambda p: p['name'])
        with self.assertRaisesRegex(ValueError, 'reviewed lock'):
            expand.plan(self.base, [self.overlay], self.lock)
        self.lock['replacements'] = {after['name']: {'before': before, 'after': after}}
        pins, _ = expand.plan(self.base, [self.overlay], self.lock)
        self.assertIn(after, pins)

    def test_unsafe_and_duplicate_overlay_names_are_refused(self):
        self.write_manifest([{**self.pin, 'name': '../escape.sty'}])
        with self.assertRaisesRegex(ValueError, 'unsafe resource path'):
            expand.plan(self.base, [self.overlay], self.lock)
        self.write_manifest([self.pin, self.pin])
        with self.assertRaisesRegex(ValueError, 'duplicate manifest name'):
            expand.plan(self.base, [self.overlay], self.lock)


if __name__ == '__main__':
    unittest.main()
