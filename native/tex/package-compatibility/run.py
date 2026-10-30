#!/usr/bin/env python3
"""Finite fresh-module WASM passes; only synthetic project fixtures, no host TeX."""
import argparse
import json
from pathlib import Path
import subprocess

HERE = Path(__file__).resolve().parent
ENGINES = ('pdftex', 'latex', 'xetex', 'luatex')


def read_json(file):
    try:
        return json.loads(file.read_text())
    except (ValueError, OSError) as error:
        raise RuntimeError(f'Invalid result: {file}') from error


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--overlay', type=Path, default=Path('/control/package-overlay'))
    parser.add_argument('--out', type=Path, default=Path('/control/final-results'))
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=False)
    commands = []
    results = []

    def invoke(command):
        done = subprocess.run(command, capture_output=True, text=True, timeout=60, check=False)
        commands.append({'argv': command, 'returncode': done.returncode, 'stdout': done.stdout, 'stderr': done.stderr})
        (args.out / 'commands.json').write_text(json.dumps(commands, indent=2) + '\n')
        print(done.stdout, end='', flush=True)
        assert done.returncode == 0, done.stderr

    def probe(engine, fixture, label, carry=None, operation='latex', expected=True):
        out = args.out / label
        invoke(['node', str(HERE / 'probe.cjs'), engine, str(HERE / 'fixtures' / (fixture + '.tex')),
                str(out), str(args.overlay), str(carry) if carry else '-', operation])
        result = read_json(out / 'result.json')
        assert result['success'] is expected, label
        if expected:
            assert result['status'] == 0 and not result['missingGlyphs'] and not result['packageErrors'], label
        else:
            artifacts = [p for p in result['outputs'] if Path(p['name']).suffix in {'.pdf', '.dvi', '.xdv'}]
            assert result['status'] != 0 and not artifacts
            assert 'package-compatibility-deliberately-absent' in (out / 'stdout.log').read_text()
        results.append({'label': label, 'engine': engine, 'success': result['success'], 'status': result['status'], 'outputs': result['outputs']})
        return out

    def convert(engine, out):
        if engine not in ('latex', 'xetex'):
            return
        artifact = out / ('головний.' + ('dvi' if engine == 'latex' else 'xdv'))
        invoke(['node', str(HERE / 'convert.cjs'), str(artifact), str(out / 'converted'), str(args.overlay)])

    for engine in ENGINES:
        prior = None
        for ordinal in range(1, 4):
            prior = probe(engine, 'hyperref', f'hyperref-{engine}-pass{ordinal}', prior)
        final_log = (prior / 'stdout.log').read_text()
        assert 'undefined references' not in final_log and 'Rerun to get cross-references right' not in final_log
        convert(engine, prior)
        first = probe(engine, 'thesis-packages', f'thesis-{engine}-pass1')
        bib = probe(engine, 'thesis-packages', f'thesis-{engine}-bibtex', first, 'bibtex')
        second = probe(engine, 'thesis-packages', f'thesis-{engine}-pass2', bib)
        final = probe(engine, 'thesis-packages', f'thesis-{engine}-pass3', second)
        final_log = (final / 'stdout.log').read_text()
        assert 'undefined references' not in final_log and 'undefined citations' not in final_log
        assert 'Rerun to get cross-references right' not in final_log
        assert not any('Citation' in line and 'undefined' in line for line in final_log.splitlines())
        bbl = next(final.glob('*.bbl')).read_text()
        assert 'reference2' in bbl and 'bibliography marker' in bbl
        convert(engine, final)
        local = probe(engine, 'local-override', f'local-override-{engine}')
        local_result = read_json(local / 'result.json')
        assert 'PACKAGE-COMPATIBILITY-PROJECT-OVERRIDE' in (local / 'stdout.log').read_text()
        assert not any(name.endswith('/runtime/geometry.sty') or name == '/tex/geometry.sty' for name in local_result['opened'])
        probe(engine, 'missing-package', f'missing-package-{engine}', expected=False)
    (args.out / 'matrix.json').write_text(json.dumps({'schema': 1, 'results': results}, indent=2) + '\n')
    print(f'PASS: {len(results)} genuine ABI invocations, four native DVI/XDV conversions, no browser acceptance claimed.')


if __name__ == '__main__':
    main()
