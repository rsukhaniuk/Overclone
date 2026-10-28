#!/usr/bin/env python3
"""Narrow PDF stream/CMap assertion for this fixture, NOT a renderer/parser API."""
from pathlib import Path
import re
import sys
import zlib

pdf = Path(sys.argv[1]).read_bytes()
assert pdf.startswith(b'%PDF-') and b'%%EOF' in pdf[-1024:]
streams = []
for match in re.finditer(rb'stream\r?\n', pdf):
    end = pdf.find(b'endstream', match.end())
    if end < 0:
        continue
    raw = pdf[match.end():end].rstrip(b'\r\n')
    try:
        streams.append(zlib.decompress(raw))
    except zlib.error:
        continue
mapping = {}
for stream in streams:
    if b'beginbfchar' not in stream:
        continue
    # All three supplied DejaVu faces use the same Unicode for shared CIDs.
    # Refuse conflicts instead of pretending this is a general PDF font resolver.
    for body in re.findall(rb'beginbfchar(.*?)endbfchar', stream, re.S):
        for source, target in re.findall(rb'<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>', body):
            key = int(source, 16)
            value = bytes.fromhex(target.decode()).decode('utf-16-be')
            assert key not in mapping or mapping[key] == value
            mapping[key] = value
lines = []
for stream in streams:
    if b'TJ' not in stream:
        continue
    for body in re.findall(rb'\[(.*?)\]TJ', stream, re.S):
        text = ''
        for token in re.findall(rb'<[0-9A-Fa-f]+>|-?\d+', body):
            if token.startswith(b'<'):
                chars = token[1:-1].decode()
                assert len(chars) % 4 == 0
                text += ''.join(mapping[int(chars[i:i+4], 16)] for i in range(0,len(chars),4))
            else:
                try:
                    adjustment = int(token)
                except ValueError as error:
                    raise ValueError('Invalid fixture text adjustment') from error
                if adjustment <= -200:
                    text += ' '
        lines.append(text)
text = '\n'.join(lines)
expected_text = ['Український текст.', 'Дипломна робота.', 'Київ.', '1 Перевірка']
expected_text += ['Reference: 1, page 2.', 'Multi-file ALPHA.'] if '--include' in sys.argv else ['Multi-file ALPHA. Reference: 1.']
normalized = ' '.join(text.split())
for expected in expected_text:
    assert expected in normalized, f'Missing {expected!r} in {text!r}'
print(text)
print('PASS: actual compressed PDF text operators and ToUnicode maps; not raster/renderer acceptance.')
