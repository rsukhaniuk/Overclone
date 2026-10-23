#!/usr/bin/env python3
"""Freeze a real build/test candidate and an observed, independently retestable VFS."""
import hashlib
import json
from pathlib import Path
import shutil
import sys

B = Path('/control/build/xetex')
R = Path('/control/results')
candidate_name = sys.argv[1] if len(sys.argv) > 1 else 'xetex-v3'
if candidate_name in ('.', '..') or Path(candidate_name).name != candidate_name:
    raise SystemExit('Candidate name must be a single path component')
C = Path('/control/candidate') / candidate_name
C.mkdir(exist_ok=True)

def parse(path):
    try:
        return json.loads(path.read_text())
    except (OSError, ValueError) as error:
        raise SystemExit(f'Invalid required receipt: {path}') from error

def record(path):
    data = path.read_bytes()
    return dict(length=len(data), sha256=hashlib.sha256(data).hexdigest())

vfs = {e['name']: e for e in parse(B/'test-vfs.json')}
format_names, compile_names = set(), set()
compile_mode = 'capacity-pass2' if candidate_name == 'xetex-v3' else 'pass2'
for mode, names in [('format', format_names), (compile_mode, compile_names)]:
    receipt = parse(R/(mode+'.json'))
    assert receipt['status'] == 0
    for name in receipt['opened']:
        # Older test receipt prepended CWD even when the original name was absolute.
        name = name.replace('/work//tex/', '/tex/')
        if name.startswith('/tex/') and name[5:] in vfs:
            names.add(name[5:])
# ICU's genuine data supports data-dependent line breaking beyond this fixture.
# Keep it explicit even when the basic fixture does not open the archive.
compile_names.add('icudt68l.dat')
subset = B/'subset'; subset.mkdir(exist_ok=True)
for name in sorted(format_names | compile_names):
    shutil.copyfile(B/'tex'/name, subset/name)
requirements = dict(schema=1, mount='/tex/<basename>', cwd='/work',
    formatGeneration=[vfs[n] for n in sorted(format_names)],
    compilation=[vfs[n] for n in sorted(compile_names)],
    generatedFormat=dict(name='xelatex.fmt', **record(R/'xelatex.fmt')),
    scope='Observed format and fontspec/DejaVu/cross-reference fixture closure plus ICU data; not full TeX Live coverage')
(C/'required-resources.json').write_text(json.dumps(requirements, indent=2)+'\n')
for source, name in [(B/'xetex.js', 'xetex.js'), (B/'xetex.wasm', 'xetex.wasm'),
                     (R/'xelatex.fmt', 'xelatex.fmt'), (R/(compile_mode+'-головний.xdv'), 'acceptance.xdv')]:
    target = C/name
    if target.exists() and target.read_bytes() != source.read_bytes():
        raise SystemExit('Refusing to mutate frozen binary '+name)
    shutil.copyfile(source, target)
modes = ['format', 'capacity-pass1', 'capacity-pass2', 'capacity-max-state', 'count-limit', 'input-limit'] if candidate_name == 'xetex-v3' else ['format', 'pass1', 'pass2']
for mode in modes:
    shutil.copyfile(R/(mode+'.json'), C/(mode+'.json'))
    shutil.copyfile(R/(mode+'.log'), C/(mode+'.log'))
for name in ['port-inputs.json', 'commands.jsonl', 'test-vfs.json']:
    shutil.copyfile(B/name, C/name)
if candidate_name == 'xetex-v3':
    prior = Path('/control/candidate/xetex-v2')
    assert record(R/'xelatex.fmt') == record(prior/'xelatex.fmt')
    (C/'evidence-reuse.json').write_text(json.dumps(dict(
        priorCandidate=str(prior), priorManifest=record(prior/'manifest.json'),
        reusedFormat=record(prior/'xelatex.fmt'),
        reusedChecks=['font/output', 'host/shell isolation', 'cancellation', 'write/memory bounds'],
        reason='Only combined /work input-count admission changed, from 64 to 192; byte limits, generated engine inputs, format ABI and output/confinement/cancellation code are unchanged.',
        rootReportedConverterReview='Root reported v2 XDV -> reviewed DVI05 -> 19349-byte parsed PDF with Ukrainian text and resolved Section 1. Not rerun by this worker; no final app claim.'
    ), indent=2)+'\n')
owned = Path(__file__).resolve().parent
(C/'owned-source.json').write_text(json.dumps({p.name: record(p) for p in sorted(owned.iterdir()) if p.is_file()}, indent=2)+'\n')
(C/'manifest.json').write_text(json.dumps({p.name: record(p) for p in sorted(C.iterdir()) if p.is_file() and p.name != 'manifest.json'}, indent=2)+'\n')
print(f'Frozen {C}; format dependencies {len(format_names)}, compile dependencies {len(compile_names)}')
