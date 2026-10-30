#!/usr/bin/env python3
"""Bounded structural checks on these genuine synthetic PDFs, not a renderer.

Resolve ordinary and Flate-compressed PDF objects, page annotation references and
catalog outline roots. This is deliberately not a general PDF security parser.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import zlib


def number(value):
    try:
        return int(value)
    except (ValueError, TypeError) as error:
        raise RuntimeError('Malformed PDF integer') from error


def inspect(file):
    data = file.read_bytes()
    assert data.startswith(b'%PDF-') and len(data) < 8 * 1024 * 1024
    objects = {number(m[1]): m[2] for m in re.finditer(rb'(\d+)\s+0\s+obj\b(.*?)endobj', data, re.S)}
    for body in list(objects.values()):
        if not re.search(rb'/Type\s*/ObjStm', body):
            continue
        count = number(re.search(rb'/N\s+(\d+)', body)[1])
        first = number(re.search(rb'/First\s+(\d+)', body)[1])
        stream = re.search(rb'stream\r?\n(.*?)endstream', body, re.S)[1]
        if re.search(rb'/Filter\s*(?:\[\s*)?/FlateDecode', body):
            decoder = zlib.decompressobj()
            content = decoder.decompress(stream, 8 * 1024 * 1024)
            assert decoder.eof and not decoder.unconsumed_tail
        else:
            assert b'/Filter' not in body.split(b'stream')[0]
            content = stream
        header = [number(n) for n in content[:first].split()]
        assert len(header) == count * 2
        for i in range(count):
            end = first + header[2 * i + 3] if i + 1 < count else len(content)
            objects[header[2 * i]] = content[first + header[2 * i + 1]:end]
    dictionaries = [body.split(b'stream')[0] for body in objects.values()]
    catalog = next(body for body in dictionaries if re.search(rb'/Type\s*/Catalog\b', body))
    pages = [body for body in dictionaries if re.search(rb'/Type\s*/Page\b', body)]
    annotation_ids = []
    for page in pages:
        array = re.search(rb'/Annots\s*\[(.*?)\]', page, re.S)
        if not array:
            reference = re.search(rb'/Annots\s+(\d+)\s+0\s+R', page)
            array_data = objects[number(reference[1])] if reference else b''
        else:
            array_data = array[1]
        annotation_ids.extend(number(n) for n in re.findall(rb'(\d+)\s+0\s+R', array_data))
    links = [n for n in annotation_ids if re.search(rb'/Subtype\s*/Link\b', objects[n])]
    uri_links = [n for n in links if re.search(rb'/S\s*/URI\b', objects[n]) and b'https://example.invalid/' in objects[n]]
    outlines = re.search(rb'/Outlines\s+(\d+)\s+0\s+R', catalog)
    outline_ids = []

    def walk(n):
        assert n not in outline_ids and len(outline_ids) < 100
        outline_ids.append(n)
        body = objects[n]
        assert b'/Title' in body
        for key in (b'First', b'Next'):
            child = re.search(rb'/' + key + rb'\s+(\d+)\s+0\s+R', body)
            if child:
                walk(number(child[1]))

    if outlines:
        first = re.search(rb'/First\s+(\d+)\s+0\s+R', objects[number(outlines[1])])
        if first:
            walk(number(first[1]))
    return {'file': str(file), 'length': len(data), 'sha256': hashlib.sha256(data).hexdigest(),
            'pages': len(pages), 'pageReferencedLinks': links, 'expectedUriLinks': uri_links,
            'catalogOutlinesObject': number(outlines[1]) if outlines else None, 'reachableOutlineItems': outline_ids}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('results', type=Path)
    args = parser.parse_args()
    evidence = []
    for engine in ('pdftex', 'latex', 'xetex', 'luatex'):
        case = args.results / f'thesis-{engine}-pass3'
        pdf = next(case.glob('*.pdf'))
        result = inspect(pdf)
        result['engine'] = engine
        assert len(result['expectedUriLinks']) == 1 and len(result['pageReferencedLinks']) >= 3
        # Old suite5 DVI has working hdvipdfm links but bookmark's separate dvips
        # default drops outlines. Preserve this known failure, never call it ready.
        assert bool(result['reachableOutlineItems']) == (engine != 'latex')
        evidence.append(result)
    (args.results / 'pdf-evidence.json').write_text(json.dumps(evidence, indent=2) + '\n')
    print(json.dumps(evidence, indent=2))


if __name__ == '__main__':
    main()
