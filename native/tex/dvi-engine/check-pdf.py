#!/usr/bin/env python3
"""Fixture-specific independent PDF-byte assertions, NOT a general PDF renderer.
Checks Flate/object streams, page count, embedded CFF font links and text/mapping.
Master must still validate the result using its existing renderer.
"""
import hashlib
import json
from pathlib import Path
import re
import sys
import unicodedata
import zlib

def integer(value, base=10):
    try:
        return int(value, base)
    except ValueError:
        raise AssertionError('Invalid fixture integer') from None


def decimal(value):
    try:
        return float(value)
    except ValueError:
        raise AssertionError('Invalid fixture decimal') from None


pdf = Path(sys.argv[1]).read_bytes()
assert pdf.startswith(b'%PDF-') and b'%%EOF' in pdf[-40:]
objects = {}
streams = {}
# The test PDF uses literal /Length and generation zero. No imported PDF is
# processed by this test tool; product parsing stays in the existing renderer.
for match in re.finditer(rb'(\d+) 0 obj\s*', pdf):
    number = integer(match[1])
    end = pdf.find(b'endobj', match.end())
    body = pdf[match.end():end]
    marker = re.search(rb'\r?\nstream\r?\n', body)
    if not marker:
        objects[number] = body
        continue
    header = body[:marker.start()]
    length = integer(re.search(rb'/Length\s+(\d+)', header)[1])
    raw = pdf[match.end() + marker.end():match.end() + marker.end() + length]
    data = zlib.decompress(raw) if b'/FlateDecode' in header else raw
    streams[number] = data
    objects[number] = header
    if b'/Type/ObjStm' in header:
        first = integer(re.search(rb'/First\s+(\d+)', header)[1])
        count = integer(re.search(rb'/N\s+(\d+)', header)[1])
        pairs = list(map(integer, data[:first].split()))
        assert len(pairs) == count * 2
        for i in range(count):
            stop = first + pairs[2 * (i + 1) + 1] if i + 1 < count else len(data)
            objects[pairs[2 * i]] = data[first + pairs[2 * i + 1]:stop]
pages = [body for body in objects.values() if re.search(rb'/Type/Page(?!s)', body)]
assert len(pages) == 2
fonts = []
for number, body in objects.items():
    name = re.search(rb'/FontName/([^/<>\s]+)', body)
    file_ref = re.search(rb'/FontFile3\s+(\d+) 0 R', body)
    if name and file_ref:
        stream_number = integer(file_ref[1])
        font = streams[stream_number]
        assert font.startswith(b'\x01\x00') and b'/Subtype/Type1C' in objects[stream_number]
        assert name[1] in font
        fonts.append({'name': name[1].decode('ascii'), 'fontStream': stream_number,
                      'bytes': len(font), 'sha256': hashlib.sha256(font).hexdigest()})
assert sorted(f['name'].split('+')[1] for f in fonts) == ['CMMI10', 'CMR10', 'CMR7']
assert any(b'<0F> <FB04>' in data for data in streams.values()), 'genuine ToUnicode ligature mapping'

def literal(data):
    def escape(m):
        token = m[1]
        if token[:1] in b'01234567':
            return bytes([integer(token, 8)])
        return {b'n': b'\n', b'r': b'\r', b't': b'\t', b'b': b'\b', b'f': b'\f'}.get(token, token)
    return re.sub(rb'\\([0-7]{1,3}|.)', escape, data).decode('latin1').replace('\x0f', '\ufb04')
texts = []
for page in pages:
    ref = re.search(rb'/Contents\[(\d+) 0 R\]', page)
    data = streams[integer(ref[1])]
    chunks = []
    for array in re.findall(rb'\[(.*?)\]TJ', data):
        for token in re.finditer(rb'\(((?:\\.|[^)\\])*)\)|(-?\d+(?:\.\d+)?)', array):
            if token[1] is not None:
                chunks.append(literal(token[1]))
            elif decimal(token[2]) <= -100:
                chunks.append(' ')
    texts.append(unicodedata.normalize('NFKC', ''.join(chunks)))
assert 'Offline DVI converter: Computer Modern, math' in texts[0]
assert 'Fresh PDF output from genuine TeX-generated DVI.' in texts[1]
print(json.dumps({'pdfBytes': len(pdf), 'sha256': hashlib.sha256(pdf).hexdigest(),
                  'pages': len(pages), 'embeddedFonts': fonts, 'decodedText': texts,
                  'scope': 'fixture byte checks; independent renderer still required'}, indent=2))
