#!/usr/bin/env python3
"""Run genuine Xe non-BMP native cases in the existing offline Docker image.

The caller supplies read-only /inputs/suite and an isolated fresh results path.
This runner does not download resources or start Docker/browser processes.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

HERE = Path(__file__).resolve().parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--engine', type=Path, required=True)
parser.add_argument('--format', type=Path, required=True)
parser.add_argument('--results', type=Path, required=True)
parser.add_argument('--reference-engine', type=Path)
parser.add_argument('--reference-format', type=Path)
args = parser.parse_args()
out = args.results.resolve()
if out.exists():
    raise SystemExit('fresh results required')
out.mkdir(parents=True)
env = dict(os.environ, ENGINE=str(args.engine.resolve()), ENGINE_FORMAT=str(args.format.resolve()),
           RESULTS=str(out), TZ='UTC')
fixed = HERE.parent.parent / 'synctex-fixtures/dvi/fixed-clock.cjs'
commands = []


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def run(label, script, mode=None, extra=None):
    command = ['node', '-r', str(fixed), str(script)] + ([mode] if mode else [])
    started = time.monotonic()
    child_env = dict(env, **(extra or {}))
    with (out / (label + '.runner.log')).open('wb') as log:
        result = subprocess.run(command, env=child_env, stdout=log, stderr=subprocess.STDOUT, timeout=60)
    commands.append({'label': label, 'argv': command, 'environment': extra or {},
                     'exit': result.returncode, 'elapsedSeconds': time.monotonic() - started})
    (out / 'commands.json').write_text(json.dumps(commands, indent=2) + '\n')
    if result.returncode:
        raise SystemExit(label + ' failed; see ' + str(out / (label + '.runner.log')))
    print(label + ': pass', flush=True)


runtime = HERE / 'test-runtime.cjs'
for mode in ['positive', 'font', 'bmp', 'failure', 'read-traversal', 'overlong',
             'output-collision', 'collision', 'bib-pass1', 'bibtex', 'bib-pass2', 'bib-pass3']:
    run(mode, runtime, mode)
for i, name in enumerate(['../secret.tex', 'x/../../secret.tex', '/etc/passwd',
                          '/tex/latex.ltx', 'bad"name.tex', r'bad\name.tex',
                          'a//b.tex', 'x|foo.tex', 'a' * 241 + '.tex']):
    label = 'invalid-main-' + str(i)
    run(label, runtime, 'invalid-main', {'INVALID_MAIN': name, 'RESULTS': str(out / label)})
for i, value in enumerate(['', '78c0802e746578', '78eda0802e746578', '78f49080802e746578',
                           '78f08080802e746578', '78f09f982e746578', '78802e746578']):
    # Empty is an explicit empty C string; the runtime harness default is avoided.
    label = 'invalid-utf8-' + str(i)
    run(label, runtime, 'invalid-utf8', {'INVALID_UTF8_HEX': value or '00', 'RESULTS': str(out / label)})
for i, name in enumerate(['😀/тези.tex', 'тези/\U00010000.tex', 'тези/\U0010ffff.tex']):
    label = 'scalar-boundary-' + str(i)
    run(label, runtime, 'positive', {'ROOT_NAME': name, 'RESULTS': str(out / label)})
run('cancellation', HERE / 'test-cancel.cjs')

if bool(args.reference_engine) != bool(args.reference_format):
    raise SystemExit('reference engine and format must be supplied together')
if args.reference_engine:
    ref = out / 'reference'
    run('baseline-rejection', runtime, 'baseline-rejection', {
        'ENGINE': str(args.reference_engine.resolve()),
        'ENGINE_FORMAT': str(args.reference_format.resolve()), 'RESULTS': str(ref)})
    compat = HERE.parent.parent / 'synctex-fixtures/xe/test-compat.cjs'
    before, after = out / 'compat-before', out / 'compat-after'
    run('bmp-compat-before', compat, extra={'ENGINE': str(args.reference_engine.resolve()),
        'ENGINE_FORMAT': str(args.reference_format.resolve()), 'RESULTS': str(before)})
    run('bmp-compat-after', compat, extra={'RESULTS': str(after), 'COMPARE_TO': str(before)})
    if (before / 'map.synctex').read_bytes() != (after / 'map.synctex').read_bytes():
        raise SystemExit('complete BMP native map changed')

receipt = {'schemaVersion': 1, 'status': 'pass', 'commands': commands, 'skips': 0,
           'identities': {'engineLoader': sha(args.engine / 'engine.js'),
                          'engineWasm': sha(args.engine / 'engine.wasm'), 'format': sha(args.format),
                          'suitePins': sha('/inputs/suite/pins.json')},
           'tooling': {p.name: sha(p) for p in [Path(__file__), runtime, HERE / 'test-cancel.cjs']},
           'scope': 'Native original UTF-8 paths, source maps, includes, fonts, diagnostics, BibTeX, '
                    'admission and worker cancellation; browser/product adoption remains separate.'}
(out / 'receipt.json').write_text(json.dumps(receipt, indent=2, ensure_ascii=False) + '\n')
print(json.dumps({'status': 'pass', 'nativeCommands': len(commands), 'skips': 0}))
