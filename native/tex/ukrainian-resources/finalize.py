#!/usr/bin/env python3
"""Freeze strict candidate manifest and explicit replacement preimages."""
import hashlib
import json
from pathlib import Path
import re
import tarfile

OUT = Path('/control/ukrainian-overlay')
SUITE = Path('/inputs/suite')

def read_json(path):
    try:
        return json.loads(path.read_text())
    except (OSError, ValueError) as error:
        raise SystemExit(f'Invalid input {path}: {error}') from error

def pin(data):
    return {'length': len(data), 'sha256': hashlib.sha256(data).hexdigest()}

def save(name, data):
    (OUT / name).write_text(json.dumps(data, indent=2) + '\n')

def main():
    provenance = read_json(OUT / 'provenance.json')
    original = {p['name']: p for p in read_json(SUITE / 'pins.json')}
    manifest, replacements, unchanged = [], [], []
    for source in sorted((OUT / 'files').rglob('*')):
        if not source.is_file():
            continue
        name = source.relative_to(OUT / 'files').as_posix()
        entry = {'name': name, **pin(source.read_bytes())}
        manifest.append(entry)
        if name not in original:
            continue
        if entry == original[name]:
            unchanged.append(name)
        else:
            replacements.append({'name': name, 'before': original[name], 'after': entry})
    assert {p['name'] for p in replacements} == {
        'hyphen.cfg', 'pdflatex.fmt', 'latex-dvi.fmt', 'xelatex.fmt', 'lualatex.fmt'}
    for item in provenance['files']:
        actual = next(p for p in manifest if p['name'] == item['name'])
        assert actual['sha256'] == item['sha256'] and actual['length'] == item['length']
    save('manifest.json', {'schema': 1, 'files': manifest})
    save('replacements.json', {'schema': 1, 'basePins': provenance['basePins'],
                              'replacements': replacements, 'unchangedRetainedFiles': unchanged})
    # Exact map-associated missing inputs, not invented substitute faces.
    archive = Path('/inputs/t2a-assets/lhcyr.r77838.tar.xz')
    with tarfile.open(archive, 'r:xz') as package:
        metrics = {Path(m.name).name: {'member': m.name, 'length': m.size}
                   for m in package.getmembers() if m.name.startswith('fonts/tfm/lhcyr/lh-t2a/')}
    index = Path('/inputs/t2a-assets/cm-super-pfb-index.html')
    try:
        sizes = {name: int(size) for name, size in re.findall(
            r'<a href="([^"]+\.pfb)">[^<]+</a>[^\n]*?\s(\d+)\s*\r?\n', index.read_text())}
    except (OSError, ValueError) as error:
        raise SystemExit(f'Cannot parse retained font index: {error}') from error
    fd = (OUT / 'files/t2acmr.fd').read_text()
    families = re.findall(r'\\EC@family\{T2A\}\{cmr\}\{([^}]+)\}\{([^}]+)\}\{([^}]+)\}', fd)
    design_sizes = [500, 600, 700, 800, 900, 1000, 1095, 1200, 1440, 1728, 2074, 2488, 2986, 3583]
    mapping = {}
    for line in (SUITE / 'files/pdftex.map').read_text().splitlines():
        match = re.match(r'(la\w+\d{4}) .* <(sf\w+\.pfb)$', line)
        if match:
            mapping[match[1]] = match[2]
    missing = []
    for series, shape, prefix in families:
        for design in design_sizes:
            stem = f'{prefix}{design:04}'
            tfm, pfb = stem + '.tfm', mapping.get(stem)
            if (OUT / 'files' / tfm).exists() and pfb and (OUT / 'files' / pfb).exists():
                continue
            missing.append({'series': series, 'shape': shape, 'designPt': design / 100,
                            'tfm': tfm, 'cachedMetric': metrics.get(tfm),
                            'pfb': pfb, 'pfbCachedIndexLength': sizes.get(pfb),
                            'metricAcquisitionNeeded': tfm not in metrics,
                            'outlineAcquisitionNeeded': True})
    save('missing-fonts.json', {'schema': 1, 'family': 'T2A/cmr',
        'supported': [{'series': 'm', 'shape': 'n', 'designPt': 10},
                      {'series': 'bx', 'shape': 'n', 'designPt': 10}],
        'completeFamily': False, 'missing': missing,
        'cachedMetricArchive': {'path': str(archive), **pin(archive.read_bytes())},
        'cachedOutlineIndex': {'path': str(index), **pin(index.read_bytes())},
        'map': {'path': '/inputs/suite/files/pdftex.map', **pin((SUITE / 'files/pdftex.map').read_bytes())},
        'outlineSource': 'https://mirrors.ctan.org/fonts/ps-type1/cm-super/pfb/<pfb>',
        'metricSource': 'CTAN LH fonts / TeX Live lh package; fonts/tfm/public/lh/lh-t2a/<tfm>. Existing lhcyr.r77838 archive has only 16 T2A metrics.',
        'outlineLicense': 'CM-Super GPL version 2; exact retained COPYING and INSTALL included.',
        'metricLicense': 'Obtain/verify LH package font notices with additional metrics; existing source-terms.md records retained evidence, not blanket redistribution clearance.',
        'sizeCaveat': 'Cached HTML lengths are upstream index claims, NOT acquired byte hashes; unavailable metric sizes remain unknown. No downloads performed.'})
    print(f'Frozen {len(manifest)} files; {len(replacements)} explicit replacements; {len(missing)} missing shape/size pairs.')

if __name__ == '__main__':
    main()
