#!/usr/bin/env python3
"""Focused offline packager refusals plus the actual supplied Lua collision closure."""
import argparse
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import prepare
import assemble


class PackagingChecks(unittest.TestCase):
    def test_untrusted_paths_tamper_duplicate_and_symlink(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            data = b'actual input bytes'
            (root / 'test.sty').write_bytes(data)
            pin = {'name': 'test.sty', 'length': len(data), 'sha256': prepare.digest(data)}
            self.assertEqual(prepare.read_verified(root, pin), data)
            with self.assertRaisesRegex(ValueError, 'duplicate source'):
                prepare.freeze([('texmf', root, [pin, pin])])
            (root / 'test.sty').write_bytes(b'tampered')
            with self.assertRaisesRegex(ValueError, 'hash/length mismatch'):
                prepare.read_verified(root, pin)
            (root / 'link.sty').symlink_to(root / 'test.sty')
            with self.assertRaisesRegex(ValueError, 'symlink'):
                prepare.read_verified(root, {**pin, 'name': 'link.sty'})
        for name in ('../file.sty', '/absolute', 'x/../y', 'x//y', 'x\\y', 'x:y', 'x\nfile'):
            with self.assertRaises(ValueError):
                prepare.safe_name(name)

    def test_limits_and_differing_collision_refuse(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name, data in [('a.sty', b'aaaa'), ('b.sty', b'bbbb'), ('nested/a.sty', b'different')]:
                path = root / name
                path.parent.mkdir(exist_ok=True)
                path.write_bytes(data)
            pins = [{'name': name, 'length': (root/name).stat().st_size,
                     'sha256': prepare.digest((root/name).read_bytes())}
                    for name in ('a.sty', 'b.sty', 'nested/a.sty')]
            with patch.object(prepare, 'MAX_FILE', 3), self.assertRaisesRegex(ValueError, 'oversized'):
                prepare.read_verified(root, pins[0])
            for ceiling in ('MAX_COUNT', 'MAX_BYTES'):
                with patch.object(prepare, ceiling, 1), self.assertRaisesRegex(ValueError, 'exceeds suite limits'):
                    prepare.freeze([('texmf', root, pins[:2])])
            with self.assertRaisesRegex(ValueError, 'differing destination collision'):
                prepare.freeze([('texmf', root, [pins[0], pins[2]])])

    def test_actual_qualified_lua_collisions_and_accepted_overrides(self):
        manifest = prepare.read_json(ARGS.common / 'common.json')
        self.assertEqual(manifest, prepare.read_json(Path(__file__).with_name('common.lock.json')))
        runtime = {p['name']: p for p in manifest['files']}
        host = prepare.read_json(ARGS.texmf / 'manifest.json')['files']
        lua_by_basename = {}
        for pin in host:
            if pin['name'].endswith('.lua'):
                lua_by_basename.setdefault(Path(pin['name']).name, []).append(pin)
        collisions = {name: pins for name, pins in lua_by_basename.items() if len({p['sha256'] for p in pins}) > 1}
        self.assertEqual(set(collisions), {'doc.lua', 'library.lua', 'Edge.lua', 'layered.lua', 'functions.lua', 'parser.lua'})
        for pins in collisions.values():
            for pin in pins:
                name = prepare.runtime_name(pin['name'])
                self.assertIn('/', name)
                self.assertEqual(runtime[name]['sha256'], pin['sha256'])
        old = prepare.read_json(ARGS.accepted_dvi / 'pins.json')
        for pin in old:
            if pin['name'] not in assemble.OLD_COMPONENTS:
                self.assertEqual(runtime[pin['name']], pin)
        provenance = prepare.read_json(ARGS.common / 'provenance.private.json')
        self.assertEqual({p['name'] for p in provenance['resolutions']}, {'pdflatex.ini', 'hyphen.cfg'})
        self.assertNotIn('icudt68l.dat', runtime)
        self.assertFalse(any(name.endswith(('.fmt', '.js', '.wasm')) for name in runtime))

    def test_previous_base01_refuses_assembly(self):
        previous = prepare.read_json(ARGS.rejected_dvi / 'pins.json')
        self.assertEqual(assemble.closure(sorted(previous, key=lambda p: p['name'])),
                         '8c02664ab8222fef7f1590a8a1872ebdd7d9eb32aa159743079f7c0250b9fe90')
        with self.assertRaisesRegex(ValueError, 'requires the exact accepted suite-base04 507-entry base'):
            assemble.plan(ARGS.common, ARGS.rejected_dvi, [])

    def test_missing_final_components_refuses_assembly(self):
        # Validates all current common/base bytes, then rejects the genuinely pending inputs.
        # Deliberately does not invent a Lua pin or emit a provisional suite ZIP.
        with self.assertRaisesRegex(ValueError, 'requires exactly one final xetex loader'):
            assemble.plan(ARGS.common, ARGS.accepted_dvi, [])


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--common', type=Path, required=True)
    parser.add_argument('--accepted-dvi', type=Path, required=True)
    parser.add_argument('--texmf', type=Path, required=True)
    parser.add_argument('--rejected-dvi', type=Path, required=True, help='preserved previous base01 fixture')
    ARGS, remaining = parser.parse_known_args()
    unittest.main(argv=[__file__, *remaining])
