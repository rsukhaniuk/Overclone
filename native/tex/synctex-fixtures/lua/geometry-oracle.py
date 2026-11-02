#!/usr/bin/env python3
"""Fixture-only independent uncompressed PDF operator/ToUnicode oracle.
No product parser or geometry inference. Raw text baselines are read FIRST,
then transformed query matches are compared. Native and rewritten controls are
reported separately; rewritten PDFs never grant native sealed-output authority.
"""
import hashlib
import json
import math
import os
from pathlib import Path
import re
import shutil
import subprocess

out = Path(os.environ['RESULTS'])
query = Path(os.environ.get('SYNCTEX_QUERY', '/inputs/generators/texk/web2c/synctex'))
sha = lambda b: hashlib.sha256(b).hexdigest()
work = out / 'query-oracle'
work.mkdir(exist_ok=True)
checks, results, commands, controls = 0, [], [], []


def integer(value):
    try:
        return int(value)
    except (ValueError, TypeError) as error:
        raise AssertionError('invalid fixture integer') from error


def number(value):
    try:
        return float(value)
    except (ValueError, TypeError) as error:
        raise AssertionError('invalid fixture number') from error


def objects(pdf):
    pairs = list(re.finditer(rb'(?:^|\n)(\d+) 0 obj\n(.*?)\nendobj', pdf, re.S))
    return {integer(m[1]): m[2] for m in pairs}


def box(page, key):
    m = re.search(rb'/' + key + rb'\s*\[([^]]+)\]', page)
    assert m is not None
    value = [number(n) for n in m[1].split()]
    assert len(value) == 4 and value[0] < value[2] and value[1] < value[3]
    return value


def inspect(pdf):
    obj = objects(pdf)
    cmap = {}
    for value in obj.values():
        for body in re.findall(rb'beginbfchar(.*?)endbfchar', value, re.S):
            for a, b in re.findall(rb'<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>', body):
                cmap[int(a, 16)] = bytes.fromhex(b.decode()).decode('utf-16-be')
    trees = [value for value in obj.values() if re.search(rb'/Type\s*/Pages\b', value)]
    assert len(trees) == 1
    kids = re.search(rb'/Kids\s*\[([^]]+)\]', trees[0])
    assert kids is not None
    ids = [integer(n) for n in re.findall(rb'(\d+) 0 R', kids[1])]
    observed = []
    for page_index, page_id in enumerate(ids):
        p = obj[page_id]
        assert re.search(rb'/Type\s*/Page\b', p)
        media, crop = box(p, b'MediaBox'), box(p, b'CropBox')
        rotate = re.search(rb'/Rotate (\d+)', p)
        contents = re.search(rb'/Contents (\d+) 0 R', p)
        assert rotate is not None and contents is not None
        stream = obj[integer(contents[1])]
        assert b'/Filter' not in stream and b' cm' not in stream
        for tm in re.finditer(rb'1 0 0 1 ([-\d.]+) ([-\d.]+) Tm \[(.*?)\]TJ', stream, re.S):
            text = ''
            for token in re.findall(rb'<([0-9A-Fa-f]+)>', tm[3]):
                h = token.decode()
                assert len(h) % 4 == 0
                text += ''.join(cmap[int(h[i:i+4], 16)] for i in range(0, len(h), 4))
            observed.append({'text': text, 'page': page_index + 1, 'x': number(tm[1]), 'y': number(tm[2]),
                             'media': media, 'crop': crop, 'rotation': integer(rotate[1])})
    return observed


def transform(m, x, y):
    a, b, c, d, e, f = m
    return a*x+c*y+e, b*x+d*y+f


def inverse(m, x, y):
    a, b, c, d, e, f = m
    det = a*d-b*c
    assert math.isfinite(det) and det != 0
    return (d*(x-e)-c*(y-f))/det, (-b*(x-e)+a*(y-f))/det


def display(point, media, crop, rotation):
    l, b = max(media[0], crop[0]), max(media[1], crop[1])
    r, t = min(media[2], crop[2]), min(media[3], crop[3])
    x, y = point
    assert l <= x <= r and b <= y <= t
    w, h = r-l, t-b
    if rotation == 0:
        return ((x-l)/w, (t-y)/h)
    if rotation == 90:
        return ((y-b)/h, (x-l)/w)
    if rotation == 180:
        return ((r-x)/w, (y-b)/h)
    assert rotation == 270
    return ((t-y)/h, (r-x)/w)


def undisplay(point, media, crop, rotation):
    l, b = max(media[0], crop[0]), max(media[1], crop[1])
    r, t = min(media[2], crop[2]), min(media[3], crop[3])
    u, v = point
    if rotation == 0:
        return l+u*(r-l), t-v*(t-b)
    if rotation == 90:
        return l+v*(r-l), b+u*(t-b)
    if rotation == 180:
        return r-u*(r-l), b+v*(t-b)
    assert rotation == 270
    return r-v*(r-l), t-u*(t-b)


def run(args, name):
    p = subprocess.run([str(query)] + args, capture_output=True, text=True, check=True)
    commands.append({'argv': [str(query)] + args, 'exit': p.returncode})
    (work / name).write_text(p.stdout + p.stderr)
    return p.stdout


# Source expectations come from exact fixture lines, not query-selected nodes.
expect = [
    ('ROOTMARK', 12, '/work/project/./розділ/geometry thesis.tex', 15),
    ('STYLEMARK', 4, '/work/project/стилі/local style.sty', 4),
    ('INCLUDEMARK', 2, '/work/project/глави/chapter one.tex', 2),
    ('TWOMARK', 21, '/work/project/./розділ/geometry thesis.tex', 22),
    ('THREEMARK', 28, '/work/project/./розділ/geometry thesis.tex', 29),
    ('FOURMARK', 34, '/work/project/./розділ/geometry thesis.tex', 35),
]
for label in ('controls', 'fallback'):
    native_pdf = (out / (label + '.pdf')).read_bytes()
    # Independent PDF baselines are read without loading the map or sidecar.
    observed = inspect(native_pdf)
    assert [p['text'] for p in observed] == [e[0] for e in expect]
    checks += 1
    try:
        geometry = json.loads((out / (label + '.geometry.json')).read_bytes())
    except (OSError, ValueError) as error:
        raise AssertionError('invalid actual geometry') from error
    assert geometry['schemaVersion'] == 1 and len(geometry['pages']) == 4
    for ext in ('pdf', 'synctex'):
        shutil.copyfile(out / (label + '.' + ext), work / (label + '.' + ext))
    for native, (text, line, filename, inverse_line) in zip(observed, expect):
        page = geometry['pages'][native['page']-1]
        assert page['status'] == 'available' and page['reason'] == 'none'
        matrix = page['writerToPdfRaw']
        body = run(['view', '-i', f'{line}:1:{filename}', '-o', str(work / (label + '.pdf'))], f'{label}-{text}-forward.txt')
        fields = {k: number(v) for k, v in re.findall(r'^(Page|x|y|h|v|W|H):([-\d.]+)$', body, re.M)}
        assert fields['Page'] == native['page']
        point = transform(matrix, fields['x'], fields['y'])
        error = abs(point[1] - native['y'])
        assert error <= .002, f'{native}: {fields}: {matrix}'
        left = transform(matrix, fields['h'], fields['y'])[0]
        right = transform(matrix, fields['h']+fields['W'], fields['y'])[0]
        assert left-.002 <= native['x'] <= right+.002
        assert fields['H'] < 10
        click = (native['x']+5, native['y'])
        h, v = inverse(matrix, *click)
        body = run(['edit', '-o', f"{native['page']}:{h}:{v}:{work / (label+'.pdf')}"], f'{label}-{text}-inverse.txt')
        inputs = re.findall(r'^Input:(.*)$', body, re.M)
        lines = [integer(n) for n in re.findall(r'^Line:(\d+)$', body, re.M)]
        assert inputs and all(n == filename for n in inputs), body
        assert lines and all(n == inverse_line for n in lines), body
        if label == 'fallback' and native['page'] == 1:
            # Native text exists but the unchanged crop intentionally clips it.
            assert click[1] > native['crop'][3]
            normalized = None
        else:
            normalized = display(click, native['media'], native['crop'], native['rotation'])
            raw = undisplay(normalized, native['media'], native['crop'], native['rotation'])
            assert max(abs(a-b) for a, b in zip(raw, click)) < 1e-10
        results.append({'fixture': label, 'pdfText': native, 'query': fields,
                        'transformedQueryBaseline': point, 'baselineErrorBp': error,
                        'inverseInput': filename, 'inverseLines': lines,
                        'displayOracleOnly': normalized})
        checks += 9
    if label == 'fallback':
        # page_height_par=0 while native page_size.v is inferred from shipped box.
        # A guessed MediaBox-height flip is demonstrably wrong in this cell.
        p = geometry['pages'][0]
        assert p['writerToPdfRaw'][5] == 0
        assert p['writerPageBoxBp'][1] < 0 and p['writerPageBoxBp'][3] == 0
        checks += 2

# Valid nonzero/negative MEDIA origins: independent rewritten-PDF controls only.
# Pinned Lua writer always writes zero origin and cannot natively produce these
# without duplicate MediaBox keys; do NOT promote them as same-pass native proof.
base = (out / 'controls.pdf').read_bytes()
for origin in ((20, 30), (-40, -20)):
    obj = objects(base)
    for k, value in list(obj.items()):
        if not re.search(rb'/Type\s*/Page\b', value):
            continue
        media = box(value, b'MediaBox')
        x, y = origin
        replacement = f'/MediaBox [{x} {y} {x+media[2]} {y+media[3]}]'.encode()
        obj[k] = re.sub(rb'/MediaBox\s*\[[^]]+\]', lambda _: replacement, value)
    trailer = re.search(rb'trailer\s*<<(.*?)>>', base, re.S)
    assert trailer is not None
    root = re.search(rb'/Root\s+(\d+ 0 R)', trailer[1])
    assert root is not None
    pdf = bytearray(b'%PDF-1.7\n%\xe2\xe3\xcf\xd3\n')
    offsets = {0: 0}
    for k in sorted(obj):
        offsets[k] = len(pdf)
        pdf += f'{k} 0 obj\n'.encode()+obj[k]+b'\nendobj\n'
    start = len(pdf)
    size = max(obj)+1
    pdf += f'xref\n0 {size}\n'.encode()+b'0000000000 65535 f \n'
    for k in range(1, size):
        pdf += f'{offsets.get(k, 0):010d} 00000 {"n" if k in obj else "f"} \n'.encode()
    pdf += b'trailer\n<< /Size '+str(size).encode()+b' /Root '+root[1]+b' >>\nstartxref\n'+str(start).encode()+b'\n%%EOF\n'
    name = f'media-origin-{origin[0]}-{origin[1]}.pdf'
    (out / name).write_bytes(pdf)
    actual = inspect(bytes(pdf))
    assert [p['text'] for p in actual] == [e[0] for e in expect]
    for p in actual:
        click = p['x']+5, p['y']
        normalized = display(click, p['media'], p['crop'], p['rotation'])
        raw = undisplay(normalized, p['media'], p['crop'], p['rotation'])
        assert max(abs(a-b) for a, b in zip(raw, click)) < 1e-10
        checks += 1
    controls.append({'path': name, 'sha256': sha(pdf), 'length': len(pdf), 'origin': origin,
                     'nativeProduced': False, 'scope': 'valid PDF display-domain oracle only; no producer/renderer acceptance'})
receipt = {'schemaVersion': 1, 'status': 'pass', 'checks': checks, 'skips': 0,
           'query': {'sha256': sha(query.read_bytes())}, 'commands': commands,
           'nativeResults': results, 'rewrittenMediaControls': controls,
           'precision': {'thresholdBp': .002, 'maxObservedBaselineErrorBp': max(r['baselineErrorBp'] for r in results),
                         'basis': 'PDF decimal text serialization and query rounding, not a compliance tolerance'},
           'nativeMediaOrigin': 'OPEN: pinned pdfgen.c unconditionally emits zero-origin MediaBox; override duplicates key',
           'rendererCssAcceptance': 'NOT RUN; display controls are independent oracle equations only',
           'inputs': {label+'.'+ext: sha((out/(label+'.'+ext)).read_bytes())
                      for label in ('controls', 'fallback') for ext in ('pdf', 'synctex', 'geometry.json')}}
(out / 'geometry-oracle.json').write_text(json.dumps(receipt, ensure_ascii=False, indent=2)+'\n')
print(json.dumps({'status': 'pass', 'checks': checks, 'skips': 0, 'precision': receipt['precision']}))
