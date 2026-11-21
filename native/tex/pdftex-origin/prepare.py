#!/usr/bin/env python3
"""Create a private origin observer overlay on the exact rebuilt semantic pdfTeX."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

HERE = Path(__file__).resolve().parent
TEX = HERE.parent
ENGINE = {
    "engine.js": "735c17494d6ec61c1b2f000f437f343ece9af3258f2f354f11745b7b5836dde2",
    "engine.wasm": "e3136893e75d5b6b3a7b4297349c28f51423dee2c014a607d9c1cd20f75357c3",
}
INPUTS = {
    "regenerate/pdftex.web": "dd88d31249fa8a7c03827504f325dd9058084ad758c97cfb7069b587a54fe8e7",
    "synctexdir/synctex.c": "bc24052ddb3d14ebc2c242c964020ea1c79c7118b876916ae33eaee5c6020079",
    "texmfmp.h": "7aaeda34f3e3a052eb2449a0c4362550dedea53084d3132a959e7da5d0c86fd9",
    "oc_synctex_memory_sink.cpp": "45dcc4332d5c1de3e1f003f0f958919c48f754d372f29407cd201044890ce7ca",
}
BASELINE_RECEIPT = "76fb0f4773eae3c326c6030cfa9ba11401f4d89e434f579e59606c26ae3ebac2"
GENERATED_FILES = ("pdftex0.c", "pdftexini.c", "pdftexd.h", "pdftexcoerce.h", "pdftex-pool.c")
DEFINITIONS = (
    "ocpdftexoriginbegin()", "ocpdftexoriginpage()", "ocpdftexoriginbp()",
    "ocpdftexoriginend()", "ocpdftexoriginroot()", "ocpdftexoriginliteral()",
    "ocpdftexoriginunsafe",
)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def require_hash(path, expected):
    if digest(path) != expected:
        raise SystemExit(f"Preimage mismatch: {path}")


def edit(text, old, new, *, occurrences=1):
    if text.count(old) != occurrences:
        raise SystemExit(f"Unexpected native seam occurrences: {old!r}")
    return text.replace(old, new)


def patch_web(web):
    # This consumes the actual page-stream initial origin immediately after the
    # native magnification branch. Contents never gain an extra cm operation.
    web = edit(web, """    @<Adjust transformation matrix for the magnification ratio@>;
end;
pdfshipoutbegin(shipping_page);""", """    @<Adjust transformation matrix for the magnification ratio@>;
    ocpdftexoriginbegin(total_pages + 1, pdf_origin_h, pdf_origin_v, mag, one_hundred_bp);
end
else ocpdftexoriginunsafe;
pdfshipoutbegin(shipping_page);""")

    # Observe the exact fixed-point values passed to the existing MediaBox
    # printer. Change neither its rounding nor its textual output.
    web = edit(web, """procedure pdf_print_bp(s: scaled); {print scaled as |bp|}
begin
    pdf_print_real(divide_scaled(s, one_hundred_bp, fixed_decimal_digits + 2),
                   fixed_decimal_digits);
end;""", """procedure pdf_print_bp(s: scaled); {print scaled as |bp|}
var printed: integer;
begin
    printed := divide_scaled(s, one_hundred_bp, fixed_decimal_digits + 2);
    ocpdftexoriginbp(printed, fixed_decimal_digits);
    pdf_print_real(printed, fixed_decimal_digits);
end;""")

    web = edit(web, """    mediabox_given:=substr_of_str("/MediaBox", s);
    flush_str(s);
end;
if not mediabox_given then begin""", """    mediabox_given:=substr_of_str("/MediaBox", s);
    ocpdftexoriginpage(total_pages, str_start[s], length(s), mediabox_given);
    flush_str(s);
end
else ocpdftexoriginpage(total_pages, 0, 0, false);
if not mediabox_given then begin""")
    web = edit(web, """@<Generate array of annotations or beads in page@>;
pdf_end_dict""", """@<Generate array of annotations or beads in page@>;
pdf_end_dict;
ocpdftexoriginend(total_pages)""")

    web = edit(web, """procedure pdf_print_toks_ln(p: pointer); {print tokens list |p|}""", """procedure pdf_print_root_attrs(p: pointer);
var s: str_number;
begin
    s := tokens_to_string(p);
    ocpdftexoriginroot(total_pages, length(s));
    if length(s) > 0 then pdf_print_ln(s);
    flush_str(s);
end;

procedure pdf_print_toks_ln(p: pointer); {print tokens list |p|}""")
    web = edit(web, """if is_root and (pdf_pages_attr <> null) then
    pdf_print_toks_ln(pdf_pages_attr);""", """if is_root then begin
    if pdf_pages_attr <> null then pdf_print_root_attrs(pdf_pages_attr)
    else ocpdftexoriginroot(total_pages, 0);
end;""")

    # After the native \special dispatcher accepted and stripped its driver
    # prefixes, inspect only the bytes that will go into the content stream.
    web = edit(web, """    while j<str_start[s+1] do begin
       pdf_out(str_pool[j]);""", """    ocpdftexoriginliteral(j, str_start[s+1] - j);
    while j<str_start[s+1] do begin
       pdf_out(str_pool[j]);""")

    web = edit(web, """    if pdf_page_resources <> null then
        pdf_print_toks_ln(pdf_page_resources);""", """    if pdf_page_resources <> null then begin
        ocpdftexoriginunsafe;
        pdf_print_toks_ln(pdf_page_resources);
    end;""")
    web = edit(web, """    s := tokens_to_string(obj_obj_data(n));""",
               """    ocpdftexoriginunsafe;
    s := tokens_to_string(obj_obj_data(n));""")
    for tokens in ("pdf_catalog_toks", "pdf_trailer_toks"):
        web = edit(web, f"pdf_print_toks_ln({tokens});",
                   f"ocpdftexoriginunsafe; pdf_print_toks_ln({tokens});",
                   occurrences=1 if tokens == "pdf_catalog_toks" else 2)
    return web


def prepare(parent, parent_build, baseline, tools, out):
    if out.exists():
        raise SystemExit("Fresh output directory required")
    for protected in (parent, parent_build, baseline, tools, HERE, Path("/work"), Path("/inputs")):
        if out == protected or protected in out.parents:
            raise SystemExit(f"Output overlaps protected input: {protected}")
    for name, expected in ENGINE.items():
        require_hash(parent_build / name, expected)
    for name, expected in INPUTS.items():
        require_hash(parent / name, expected)
    require_hash(baseline / "candidate-hashes.json", BASELINE_RECEIPT)

    producer = json.loads((parent / "semantics-preparation.json").read_text())
    if producer["baselineManifest"] != BASELINE_RECEIPT:
        raise SystemExit("Wrong parent source closure")
    for name, expected in producer["sources"].items():
        require_hash(parent / name, expected)
    for name, expected in producer["generators"].items():
        require_hash(tools / name, expected)

    shutil.copytree(parent, out)
    generated = out / "origin-regenerate"
    logs = out / "origin-logs"
    generated.mkdir()
    logs.mkdir()
    commands = []

    def run(arguments, label, data=None):
        command = list(map(str, arguments))
        commands.append(command)
        result = subprocess.run(command, cwd=generated, input=data, capture_output=True,
                                env=dict(os.environ, WEBINPUTS=".:", LC_ALL="C"), timeout=120)
        (logs / f"{label}.stdout").write_bytes(result.stdout)
        (logs / f"{label}.stderr").write_bytes(result.stderr)
        if result.returncode:
            raise SystemExit(f"{label} failed; see {logs}")
        return result.stdout

    (generated / "pdftex.web").write_text(patch_web((parent / "regenerate/pdftex.web").read_text()))
    (generated / "empty.ch").write_text("")
    run([tools / "tangle", "pdftex.web", "empty.ch"], "tangle")
    definitions = [
        baseline / "helpers/web2c/common.defines", baseline / "helpers/web2c/texmf.defines",
        baseline / "helpers/synctexdir/synctex.defines", out / "pdftexdir/pdftex.defines",
        TEX / "synctex-producer/producer.defines", TEX / "font-semantics/semantics.defines",
    ]
    data = b"".join(file.read_bytes() for file in definitions)
    data += "".join(f"@define procedure {name};\n" for name in DEFINITIONS).encode()
    data += (generated / "pdftex.p").read_bytes()
    data = run([tools / "web2c/web2c", "-htexmfmp.h", "-t", "-cpdftexcoerce"], "web2c", data)
    data = run([tools / "web2c/fixwrites", "-t", "pdftex"], "fixwrites", data)
    run([tools / "web2c/splitup", "-i", "-l", "65000", "pdftex"], "splitup", data)
    coerce = generated / "pdftexcoerce.h"
    coerce.write_bytes(coerce.read_bytes() + (baseline / "helpers/web2c/coerce.h").read_bytes())
    (generated / "pdftex-pool.c").write_bytes(run([tools / "web2c/makecpool", "pdftex"], "makecpool"))
    for name in GENERATED_FILES:
        shutil.copyfile(generated / name, out / "tex" / name)
    require_hash(out / "tex/pdftex-pool.c", digest(parent / "tex/pdftex-pool.c"))

    for name in ("writer.h", "writer.cpp"):
        shutil.copyfile(HERE / name, out / f"oc_pdftex_origin_{name}")

    header = out / "texmfmp.h"
    # Web2C defines its scalar types after this include site. The hook header
    # deliberately only uses standard fixed types.
    header.write_text(edit(header.read_text(), '#include "oc_font_semantics.h"',
                           '#include "oc_font_semantics.h"\n#include "oc_pdftex_origin_writer.h"'))
    sink = out / "oc_synctex_memory_sink.cpp"
    sink.write_text(sink.read_text() + '\n#include "oc_pdftex_origin_writer.cpp"\n')
    sy = out / "synctexdir/synctex.c"
    sy.write_bytes(edit(sy.read_bytes().decode("latin-1"), "#   include SYNCTEX_ENGINE_H",
                       '#   include SYNCTEX_ENGINE_H\n#   include "oc_pdftex_origin_writer.h"').encode('latin-1'))
    sy.write_bytes(edit(sy.read_bytes().decode("latin-1"), """                                  ((SYNCTEX_OFFSET_IS_PDF != 0) ? 0 : 4736287 UNIT));
        if (len > 0) {
            synctex_ctxt.total_length += len;
            return SYNCTEX_NOERR;""", """                                  ((SYNCTEX_OFFSET_IS_PDF != 0) ? 0 : 4736287 UNIT));
        if (len > 0) {
            const int pdf = SYNCTEX_OFFSET_IS_PDF != 0;
            const int origin = pdf ? 0 : 4736287 UNIT;
            ocpdftexoriginmap(pdf, synctex_ctxt.magnification, synctex_ctxt.unit, origin, origin);
            synctex_ctxt.total_length += len;
            return SYNCTEX_NOERR;""").encode("latin-1"))
    receipt = {
        "schemaVersion": 1, "requiredInstalledParent": ENGINE,
        "parentPreimages": INPUTS, "baselineManifest": BASELINE_RECEIPT,
        "generators": producer["generators"], "commands": commands,
        "generated": {name: digest(out / "tex" / name) for name in GENERATED_FILES},
        "changedNativeInputs": {
            name: digest(out / name) for name in (
                "texmfmp.h", "synctexdir/synctex.c", "oc_synctex_memory_sink.cpp",
                "oc_pdftex_origin_writer.h", "oc_pdftex_origin_writer.cpp"
            )
        },
    }
    (out / "origin-preparation.json").write_text(json.dumps(receipt, indent=2) + "\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("parent", "parent-build", "baseline", "generators", "out"):
        parser.add_argument("--" + name, required=True, type=lambda path: Path(path).resolve())
    args = parser.parse_args()
    prepare(args.parent, args.parent_build, args.baseline, args.generators, args.out)
