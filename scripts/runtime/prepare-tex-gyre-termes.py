#!/usr/bin/env python3
"""Extend the pinned P21 compiler ZIP with four original TeX Gyre Termes faces."""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile


LOCK = Path(__file__).with_name("tex-gyre-termes.lock.json")
CATALOG = "xetexfontlist.txt"


def digest(data):
    return hashlib.sha256(data).hexdigest()


def file_pin(path, pin):
    if path.stat().st_size != pin["length"]:
        raise ValueError(f"Wrong size: {path.name}")
    with path.open("rb") as source:
        if hashlib.file_digest(source, "sha256").hexdigest() != pin["sha256"]:
            raise ValueError(f"Wrong digest: {path.name}")


def catalog_rows(fonts, first_index):
    # Existing XeTeX reader's record order. Style and GPOS optical-size facts
    # were extracted from the exact OpenType inputs recorded in the lock.
    for index, font in enumerate(fonts, first_index):
        style = font["style"]
        weight, width, slant = font["weightWidthSlant"]
        regular, bold, italic = font["styleFlags"]
        design, low, high, family = font["optical"]
        pt = lambda n: format(n * 72.27 / 72 / 10, ".12g")
        yield "\n".join(map(str, [
            index, "/tex/" + font["name"], 0, 1, "TeX Gyre Termes", 1, style, 1,
            "TeX Gyre Termes " + style, font["postscript"], style, weight, width,
            slant, regular, bold, italic, pt(design), pt(low), pt(high), family, family,
        ])).encode("ascii") + b"\n"


def assemble(base, font_dir, notice_path, out, lock):
    file_pin(base, lock["base"])
    for font in lock["fonts"]:
        file_pin(font_dir / font["name"], font)
    notice = lock["notice"]
    file_pin(notice_path, notice)
    with zipfile.ZipFile(base) as old:
        catalog = old.read("files/" + CATALOG)
        first_index = len(catalog.splitlines()) // 22 + 1
        resources = {font["name"]: (font_dir / font["name"]).read_bytes() for font in lock["fonts"]}
        resources[notice["name"]] = notice_path.read_bytes()
        pins = {pin["name"]: pin for pin in json.loads(old.read("pins.json"))}
        if pins.keys() & resources.keys():
            raise ValueError("Additions overlap the base compiler")
        resources[CATALOG] = catalog + b"".join(catalog_rows(lock["fonts"], first_index))
        for name, data in resources.items():
            pins[name] = dict(name=name, length=len(data), sha256=digest(data))
        rows = sorted(pins.values(), key=lambda pin: pin["name"])
        manifest = (json.dumps(rows, indent=2) + "\n").encode()
        closure = digest("\n".join(f"{p['name']}\t{p['length']}\t{p['sha256']}" for p in rows).encode())
        runtime = dict(count=len(rows), totalBytes=sum(p["length"] for p in rows), closure=closure,
                       identity=digest(f"native-runtime-profile-v1\n{lock['runtimeIdentityName']}\n{closure}".encode()))
        if runtime != lock["runtime"]:
            raise ValueError("Extended resource closure differs from the runtime lock")
        with out.open("xb") as target:
            try:
                with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_STORED, allowZip64=False) as archive:
                    archive.writestr(zipfile.ZipInfo("pins.json"), manifest)
                    for pin in rows:
                        name = pin["name"]
                        data = resources[name] if name in resources else old.read("files/" + name)
                        archive.writestr(zipfile.ZipInfo("files/" + name), data)
                file_pin(out, lock["archive"])
            except Exception:
                out.unlink()
                raise
    return runtime


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("base", "font-dir", "notice", "out"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    try:
        print(json.dumps(assemble(args.base, args.font_dir, args.notice, args.out, json.loads(LOCK.read_bytes()))))
    except (ValueError, KeyError, OSError, zipfile.BadZipFile) as error:
        parser.exit(2, f"Refused Termes compiler archive: {error}\n")
