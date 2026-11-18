#!/usr/bin/env python3
"""Generate small synthetic PDFs with independently known placement geometry."""

import argparse
from pathlib import Path


def stream(dictionary, data):
    if isinstance(data, str):
        data = data.encode("ascii")
    return f"<< {dictionary} /Length {len(data)} >>\nstream\n".encode() + data + b"\nendstream"


def image(*, width="1", extra="", data=b"\x13\x4d\xb1"):
    return stream(
        f"/Type /XObject /Subtype /Image /Width {width} /Height 1 "
        f"/ColorSpace /DeviceRGB /BitsPerComponent 8 {extra}", data,
    )


def draw(matrix="120 0 0 120 30 40", name="Im"):
    return f"q {matrix} cm /{name} Do Q\n"


def write_pdf(path, objects):
    pdf = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
    offsets = [0]
    for number, body in enumerate(objects, 1):
        offsets.append(len(pdf))
        pdf.extend(f"{number} 0 obj\n".encode())
        pdf.extend(body.encode("ascii") if isinstance(body, str) else body)
        pdf.extend(b"\nendobj\n")
    start_xref = len(pdf)
    pdf.extend(f"xref\n0 {len(offsets)}\n0000000000 65535 f \n".encode())
    for offset in offsets[1:]:
        pdf.extend(f"{offset:010d} 00000 n \n".encode())
    pdf.extend(f"trailer\n<< /Size {len(offsets)} /Root 1 0 R >>\nstartxref\n{start_xref}\n%%EOF\n".encode())
    path.write_bytes(pdf)


def make(destination):
    def fixture(name, content, *, frame="", image_object=None, parent="", second_page=False):
        first_page = (
            "<< /Type /Page /Parent 2 0 R " + (frame or "/MediaBox [0 0 220 220]") +
            " /Resources << /XObject << /Im 5 0 R /Nested 6 0 R >> "
            "/ExtGState << /Alpha 7 0 R >> /Font << /F1 8 0 R >> "
            "/Shading << /Shade 9 0 R >> >> /Contents 4 0 R >>"
        )
        objects = [
            "<< /Type /Catalog /Pages 2 0 R >>",
            f"<< /Type /Pages /Kids [3 0 R {'11 0 R' if second_page else ''}] "
            f"/Count {2 if second_page else 1} {parent} >>",
            first_page,
            stream("", content),
            image_object or image(),
            stream("/Type /XObject /Subtype /Form /BBox [0 0 220 220] "
                   "/Resources << /XObject << /Im 5 0 R >> >>", draw()),
            "<< /Type /ExtGState /ca 0.5 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 200 220] "
            "/Function << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> >>",
            stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", b"\x7f"),
        ]
        if second_page:
            objects.append(first_page.replace("4 0 R", "12 0 R"))
            objects.append(stream("", draw("60 0 0 45 20 25")))
        write_pdf(destination / (name + ".pdf"), objects)

    fixture("empty", "")
    fixture("image", draw())
    fixture("ordering", "BT /F1 12 Tf 12 18 Td (Placement check) Tj ET\n" + draw() +
            "20 185 30 12 re f\n" + draw("-75 28 18 65 148 38"), second_page=True)
    fixture("shear", draw("55 -13 16 48 54 68"))
    for rotation in (0, 90, 180, 270):
        fixture(f"frame-{rotation}", draw("70 0 0 60 10 20"),
                frame=f"/MediaBox [-30 -20 210 200] /CropBox [-8 2 178 154] /Rotate {rotation} /UserUnit 2")
    fixture("invalid-user-unit", draw(), frame="/MediaBox [0 0 220 220] /UserUnit (two)")
    fixture("parent-unit", draw(), parent="/UserUnit 3")
    fixture("cross-crop", draw(), frame="/MediaBox [0 0 220 220] /CropBox [50 50 210 210]")
    fixture("touch-crop", draw(), frame="/MediaBox [0 0 220 220] /CropBox [30 20 210 210]")
    fixture("cross-media", draw("120 0 0 120 -30 40"))
    fixture("incompatible-boxes", draw(), frame="/MediaBox [0 0 220 220] /CropBox [300 300 400 400]")
    fixture("clip", "q 40 50 70 60 re W n\n" + draw() + "Q\n")
    fixture("marked", "/Span BMC\n" + draw() + "EMC\n")
    fixture("optional", draw(), image_object=image(extra="/OC << /Type /OCG /Name (Layer) >>"))
    fixture("alpha", "q /Alpha gs\n" + draw() + "Q\n")
    fixture("group", draw(), frame="/MediaBox [0 0 220 220] /Group << /S /Transparency >>")
    fixture("mask", draw(), image_object=image(extra="/Mask [0 64 0 128 0 192]"))
    fixture("softmask", draw(), image_object=image(extra="/SMask 10 0 R"))
    fixture("stencil", draw(), image_object=stream(
            "/Type /XObject /Subtype /Image /Width 1 /Height 1 /ImageMask true /BitsPerComponent 1", b"\x80"))
    fixture("explicit-non-stencil", draw(), image_object=image(extra="/ImageMask false"))
    fixture("malformed-width", draw(), image_object=image(width="1.5"))
    fixture("degenerate", draw("70 35 20 10 60 80"))
    fixture("matrix-out-of-domain", draw("5000000 0 0 1 15 25"))
    fixture("forms-and-shading", "/Nested Do\n/Shade sh\n")
    for count in (512, 513):
        fixture(f"{count}-images", draw("24 0 0 24 45 55") * count)
    for count in (10000, 10001):
        fixture(f"{count}-objects", "q 15 30 2 2 re f Q\n" * count)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    destination = parser.parse_args().directory
    destination.mkdir(parents=True)
    make(destination)
