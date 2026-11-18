#!/usr/bin/env python3
"""Build a synthetic one-page PDF with a shared-frame image/caption witness."""

import runpy
import subprocess
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

repo, output = map(Path, sys.argv[1:])
inventory = runpy.run_path(str(repo / "native/pdf/object-layout/make_fixtures.py"))
stream, image, draw, write_pdf = (
    inventory[name] for name in ("stream", "image", "draw", "write_pdf")
)
with TemporaryDirectory() as temporary:
    subprocess.run([
        sys.executable, str(repo / "native/pdf/fixtures/ink/make_fixtures.py"), temporary,
    ], check=True)
    font = (Path(temporary) / "rect.ttf").read_bytes()

# Caption ink starts at y=80 and is between two explicit image occurrences.
# Their x projections both cover it. Paint both images before the glyphs.
content = draw("100 0 0 24 18 20") + draw("80 0 0 22 24 110")
content += "BT /F1 12 Tf 40 80 Td (AAAA) Tj ET\n"
write_pdf(output, [
    "<< /Type /Catalog /Pages 2 0 R >>",
    "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
    "<< /Type /Page /Parent 2 0 R /MediaBox [-30 -20 210 200] "
    "/CropBox [-8 2 178 154] /UserUnit 2 /Resources "
    "<< /XObject << /Im 8 0 R >> /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
    stream("", content),
    "<< /Type /Font /Subtype /TrueType /BaseFont /SyntheticInk /FirstChar 65 /LastChar 65 "
    "/Widths [600] /FontDescriptor 6 0 R /Encoding /WinAnsiEncoding >>",
    "<< /Type /FontDescriptor /FontName /SyntheticInk /FontBBox [0 0 600 800] /Flags 32 "
    "/Ascent 1000 /Descent 0 /CapHeight 800 /ItalicAngle 0 /StemV 80 /FontWeight 400 /FontFile2 7 0 R >>",
    stream(f"/Length1 {len(font)}", font),
    image(),
])
