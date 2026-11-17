"""Synthetic complete PDF with genuine native two-line font outlines.

Reuses the repository's deterministic tiny embedded TrueType fixture builder;
no system fonts, compiler or extra acquisition. Pass a fresh output directory.
"""
from pathlib import Path
import runpy, hashlib, json
base = runpy.run_path(str(Path(__file__).resolve().parents[5] / 'native/pdf/fixtures/ink/make_fixtures.py'))
data = base['pdf'](b'BT /F1 12 Tf 10 80 Td (AA) Tj 0 -20 Td (AA) Tj ET')
out = base['out']
(out / 'typography-two-line.pdf').write_bytes(data)
(out / 'typography-pdf.json').write_text(json.dumps({'fixtureKind':'synthetic-complete-pdf-genuine-native-output','sha256':hashlib.sha256(data).hexdigest(),'bytes':len(data),'oracle':{'nativeGlyphHeightRaw':9.6,'neighboringBaselineDistanceRaw':20}},indent=2)+'\n')
