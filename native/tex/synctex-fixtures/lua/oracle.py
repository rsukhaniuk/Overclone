#!/usr/bin/env python3
"""Narrow uncompressed two-page fixture oracle, NOT a product PDF parser.
Reads actual PDF text matrices/ToUnicode independently of SyncTeX, then runs the
already acquired native query. No writer matrix is inferred/promoted by this test.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess

out = Path(os.environ.get('RESULTS', '/control/r2-lua-maps/evidence'))
query = Path(os.environ.get('SYNCTEX_QUERY', '/inputs/generators/texk/web2c/synctex'))
run = out / 'runtime'
work = out / 'query-oracle'
work.mkdir(exist_ok=True)
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
def integer(value):
    try:
        return int(value)
    except (TypeError, ValueError) as error:
        raise AssertionError('Invalid fixture integer') from error


def number(value):
    try:
        return float(value)
    except (TypeError, ValueError) as error:
        raise AssertionError('Invalid fixture decimal') from error


checks = 0
commands = []
results = []
for label in ('positive', 'changed'):
    for ext in ('pdf', 'synctex'):
        shutil.copyfile(run / f'{label}.{ext}', work / f'{label}.{ext}')
    pdf = (work / f'{label}.pdf').read_bytes()
    objects = {integer(m[1]): m[2] for m in re.finditer(rb'(?:^|\n)(\d+) 0 obj\n(.*?)\nendobj', pdf, re.S)}
    cmap = {}
    for obj in objects.values():
        for body in re.findall(rb'beginbfchar(.*?)endbfchar', obj, re.S):
            for a, b in re.findall(rb'<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>', body):
                cmap[int(a, 16)] = bytes.fromhex(b.decode()).decode('utf-16-be')
    pages = [o for o in objects.values() if re.search(rb'/Type\s*/Page\b', o)]
    assert len(pages) == 2
    checks += 1
    observed = []
    for page in pages:
        media_match = re.search(rb'/MediaBox\s*\[([^]]+)\]', page)
        contents_match = re.search(rb'/Contents\s+(\d+) 0 R', page)
        assert media_match is not None and contents_match is not None
        media = list(map(number, media_match[1].split()))
        assert media[0] == 0 and media[1] == 0  # This fixture only, not producer geometry.
        contents = objects[integer(contents_match[1])]
        for tm in re.finditer(rb'1 0 0 1 ([-\d.]+) ([-\d.]+) Tm \[(.*?)\]TJ', contents, re.S):
            text = ''
            for token in re.findall(rb'<([0-9A-Fa-f]+)>', tm[3]):
                h = token.decode()
                assert len(h) % 4 == 0
                text += ''.join(cmap[int(h[i:i+4], 16)] for i in range(0, len(h), 4))
            observed.append({'text': text, 'page': pages.index(page)+1, 'x': number(tm[1]), 'y': media[3]-number(tm[2])})
    expected = [
        ('ROOTMARK' if label == 'positive' else 'ROOTSHIFT', 9, '/work/project/./розділ/my thesis.tex', 12),
        ('STYLEMARK', 4, '/work/project/стилі/local style.sty', 4),
        ('INCLUDEMARK', 2, '/work/project/глави/chapter one.tex', 2),
        ('SECONDMARK', 14, '/work/project/./розділ/my thesis.tex', 15),
    ]
    assert [o['text'] for o in observed] == [e[0] for e in expected]
    checks += 1
    for native, (text, line, filename, inverse_line) in zip(observed, expected):
        cmd = [str(query), 'view', '-i', f'{line}:1:{filename}', '-o', str(work / f'{label}.pdf')]
        result = subprocess.run(cmd, capture_output=True, text=True, check=True)
        commands.append({'argv': cmd, 'exit': result.returncode})
        (work / f'{label}-{text}-forward.txt').write_text(result.stdout + result.stderr)
        fields = {k: number(v) for k, v in re.findall(r'^(Page|x|y|h|v|W|H):([-\d.]+)$', result.stdout, re.M)}
        assert fields['Page'] == native['page']
        error = abs(fields['y']-native['y'])
        assert error <= 0.002, f'{native}: {fields}'
        assert fields['h'] <= native['x'] <= fields['h']+fields['W']+0.002
        assert fields['H'] < 10  # Reject the pre-narrowing bogus y=0/huge box.
        checks += 4
        # The click is independently chosen inside actual PDF text, not a query result.
        cmd = [str(query), 'edit', '-o', f"{native['page']}:{native['x']+5}:{native['y']}:{work / (label+'.pdf')}"]
        result = subprocess.run(cmd, capture_output=True, text=True, check=True)
        commands.append({'argv': cmd, 'exit': result.returncode})
        (work / f'{label}-{text}-inverse.txt').write_text(result.stdout + result.stderr)
        inputs = re.findall(r'^Input:(.*)$', result.stdout, re.M)
        lines = list(map(int, re.findall(r'^Line:(\d+)$', result.stdout, re.M)))
        assert inputs and all(p == filename for p in inputs), result.stdout
        assert lines and all(p == inverse_line for p in lines), result.stdout
        checks += 2
        results.append({'fixture': label, 'pdfText': native, 'forward': fields, 'baselineErrorBp': error,
                        'inverseInput': filename, 'inverseLines': lines})
assert sha(run / 'positive.synctex') != sha(run / 'changed.synctex')
assert sha(run / 'positive.pdf') != sha(run / 'changed.pdf')
checks += 2
receipt = {'schemaVersion': 1, 'status': 'pass', 'checks': checks, 'skips': 0,
           'query': {'path': str(query), 'sha256': sha(query)}, 'commands': commands, 'results': results,
           'oracle': 'Actual uncompressed PDF text operators + ToUnicode; known two-page fixture only',
           'precision': {'thresholdBp': 0.002, 'maxObservedErrorBp': max(r['baselineErrorBp'] for r in results),
                         'basis': 'PDF matrix decimal serialization (3 decimals) plus query float rounding; NOT a thesis-rule tolerance'},
           'geometryGate': 'OPEN; this zero-origin fixture does not establish a complete native writer transform',
           'inputs': {str(p.relative_to(out)): sha(p) for label in ('positive', 'changed') for p in (run / f'{label}.pdf', run / f'{label}.synctex')}}
(out / 'oracle.json').write_text(json.dumps(receipt, ensure_ascii=False, indent=2)+'\n')
print(json.dumps({'status': 'pass', 'checks': checks, 'skips': 0, 'maxObservedErrorBp': receipt['precision']['maxObservedErrorBp']}))
