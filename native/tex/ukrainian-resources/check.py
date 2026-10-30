#!/usr/bin/env python3
"""Independent assertions over actual WASM logs/output, not compile exit alone."""
import hashlib
import json
from pathlib import Path

OUT = Path('/control/ukrainian-overlay')

def read_json(path):
    try:
        return json.loads(path.read_text())
    except (OSError, ValueError) as error:
        raise SystemExit(f'Invalid evidence {path}: {error}') from error

def pin(data):
    return {'length': len(data), 'sha256': hashlib.sha256(data).hexdigest()}

def between(text, start, end):
    assert text.count(start) == 1 and text.count(end) == 1
    return text.split(start, 1)[1].split(end, 1)[0]

def main():
    summary = []
    old = {p['name']: p for p in read_json(Path('/inputs/suite/pins.json'))}
    for engine, fixture, fmt, output, magic in [
        ('pdftex', 't2a', 'pdflatex.fmt', 'main.pdf', b'%PDF-'),
        ('dvi', 't2a', 'latex-dvi.fmt', 'main.dvi', bytes([247, 2])),
        ('xetex', 'unicode', 'xelatex.fmt', 'main.xdv', bytes([247, 7])),
        ('luatex', 'unicode', 'lualatex.fmt', 'result.pdf', b'%PDF-'),
    ]:
        directory = OUT / 'checks' / engine / fixture
        result = read_json(directory / 'result.json')
        assert result['status'] == 0
        log = (directory / 'stdout.log').read_text()
        assert 'ENGLISH-ID:0\n' in log and 'ENGLISH-ACTIVE:0\n' in log
        ukrainian_id = 2 if engine == 'luatex' else 1
        assert f'UKRAINIAN-ID:{ukrainian_id}\n' in log
        assert f'UKRAINIAN-ACTIVE:{ukrainian_id}\n' in log
        assert 'ENGLISH-CONTENTS:Contents\n' in log
        for forbidden in ['Missing character:', 'No hyphenation patterns',
                          'will use the patterns', 'Font Warning:', 'not available']:
            assert forbidden not in log, f'{engine}: unexpected {forbidden}'
        uk = between(log, 'UKRAINIAN-HYPHENS-BEGIN', 'UKRAINIAN-HYPHENS-END')
        en = between(log, 'ENGLISH-HYPHENS-BEGIN', 'ENGLISH-HYPHENS-END')
        control = between(log, 'UKRAINIAN-IN-ENGLISH-CONTROL-BEGIN',
                          'UKRAINIAN-IN-ENGLISH-CONTROL-END')
        assert 'hy-phen-ation uni-ver-sity com-pat-i-bil-ity' in en
        if fixture == 'unicode':
            assert 'UKRAINIAN-CONTENTS:Зміст\n' in log
            assert 'укра-їн-ський укра-їн-ська уні-вер-си-тет' in uk
            assert 'український університет' in control
            assert 'укра-їн' not in control and 'уні-вер' not in control
        else:
            # TeX wraps its ASCII ^^xx serialization, sometimes inside ^^xx.
            # Join only log wrapping; do not fold Unicode or alter source/output.
            compact = uk.replace('\n', '')
            assert '^^f3^^ea^^f0^^e0-^^a8^^ed-^^f1^^fc^^ea^^e8^^e9' in compact
            assert '^^f3^^edi-^^e2^^e5^^f0-^^f1^^e8-^^f2^^e5^^f2' in compact
            assert '^^f3^^ea^^f0^^e0^^a8^^ed^^f1^^fc^^ea^^e8^^e9' in control.replace('\n', '')
            assert '\\CYRZ \\cyrm \\cyrii \\cyrs \\cyrt' in log
            if engine == 'pdftex':
                assert '/sfrm1000.pfb>' in log and '/sfbx1000.pfb>' in log
        data = (directory / output).read_bytes()
        assert len(data) > 100 and data.startswith(magic)
        assert pin(data) == result['outputs'][output]
        generated = (OUT / 'files' / fmt).read_bytes()
        fmt_pin = pin(generated)
        assert fmt_pin['sha256'] != old[fmt]['sha256']
        assert result['chosenFormat']['length'] == fmt_pin['length']
        assert result['chosenFormat']['sha256'] == fmt_pin['sha256']
        generation = read_json(OUT / 'checks' / engine / 'format' / 'result.json')
        assert generation['status'] == 0
        assert fmt_pin in generation['outputs'].values()
        summary.append({'engine': engine, 'englishId': 0, 'ukrainianId': ukrainian_id,
                        'ukrainianAndEnglishBreaks': True, 'englishLanguageNegativeControl': True,
                        'babelCaption': 'Зміст', 'missingGlyphs': False,
                        'format': {'name': fmt, **fmt_pin}, 'output': {'name': output, **pin(data)}})
    (OUT / 'checks' / 'summary.json').write_text(json.dumps({'schema': 1, 'checks': summary}, indent=2) + '\n')
    print('PASS: 4 genuine formats; English0/Ukrainian IDs, real breaks, negative controls, captions and output magic. No browser/extraction claim.')

if __name__ == '__main__':
    main()
