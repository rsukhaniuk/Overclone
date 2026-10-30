#!/usr/bin/env python3
"""Trust-boundary checks using explicit genuine profile6/ABI3/BST fixtures."""
import copy
import importlib.util
import os
from pathlib import Path
import shutil
import tempfile
import unittest

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('bibliography_upgrade', HERE / 'upgrade-bibliography.py')
upgrade = importlib.util.module_from_spec(spec)
spec.loader.exec_module(upgrade)


class ClosedUpgradeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        keys = ['BIBLIOGRAPHY_BASE', 'BIBLIOGRAPHY_LUA', 'BIBLIOGRAPHY_BST']
        if not all(os.environ.get(k) for k in keys):
            raise unittest.SkipTest('Requires genuine closed profile6, accepted ABI3 pair and matching local BST')
        cls.base, cls.lua, cls.bst = (Path(os.environ[k]) for k in keys)
        cls.lock = upgrade.read_json(HERE / 'bibliography.lock.json')
        cls.original_pins = (cls.base / 'pins.json').read_bytes()

    def tearDown(self):
        self.assertEqual(self.original_pins, (self.base / 'pins.json').read_bytes())

    def check(self, lock=None, lua=None, bst=None):
        return upgrade.plan(self.base, lua or self.lua, bst or self.bst, lock or self.lock)

    def test_complete_real_upgrade_preserves_all_other_bytes(self):
        pins, entries = self.check()
        old = upgrade.inventory(self.base, 'pins.json')
        self.assertEqual(3703, len(pins))
        self.assertEqual(self.lock['closureSha256'], upgrade.closure(pins))
        for name in old:
            if name not in upgrade.REPLACEMENTS:
                self.assertEqual(old[name][0], entries[name][0])

    def test_unknown_profile_is_not_an_upgrade(self):
        lock = copy.deepcopy(self.lock); lock['profile'] = 8
        with self.assertRaisesRegex(ValueError, 'wrong bibliography'):
            self.check(lock)

    def test_cannot_replace_another_engine(self):
        lock = copy.deepcopy(self.lock)
        lock['replacements']['engine.wasm'] = lock['replacements']['luatex.wasm']
        with self.assertRaisesRegex(ValueError, 'only the reviewed'):
            self.check(lock)

    def test_preimage_mismatch_cannot_relabel_an_old_component(self):
        lock = copy.deepcopy(self.lock)
        lock['replacements']['luatex.wasm']['before']['sha256'] = '0' * 64
        with self.assertRaisesRegex(ValueError, 'preimage/candidate mismatch'):
            self.check(lock)

    def test_bad_candidate_is_refused_before_any_output_is_created(self):
        with tempfile.TemporaryDirectory() as directory:
            lua = Path(directory)
            for name in upgrade.REPLACEMENTS:
                shutil.copyfile(self.lua / name, lua / name)
            with (lua / 'luatex.wasm').open('r+b') as stream:
                stream.write(b'bad!')
            with self.assertRaisesRegex(ValueError, 'preimage/candidate mismatch'):
                self.check(lua=lua)
            self.assertFalse((lua / 'pins.json').exists())

    def test_missing_or_changed_bst_cannot_be_silently_omitted(self):
        with tempfile.TemporaryDirectory() as directory:
            bst = Path(directory) / 'biblatex.bst'; bst.write_bytes(b'wrong')
            with self.assertRaisesRegex(ValueError, 'BST addition/hash mismatch'):
                self.check(bst=bst)

    def test_partial_or_ambiguous_inventory_is_not_a_closed_upgrade(self):
        lock = copy.deepcopy(self.lock); lock['files'].pop()
        with self.assertRaisesRegex(ValueError, 'complete bibliography inventory'):
            self.check(lock)


if __name__ == '__main__':
    unittest.main()
