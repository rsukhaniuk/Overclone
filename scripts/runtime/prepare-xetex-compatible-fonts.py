#!/usr/bin/env python3
"""Add documented XeTeX family aliases for the pinned Liberation faces."""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile

LOCK = Path(__file__).with_name("xetex-compatible-fonts.lock.json")
CATALOG = "xetexfontlist.txt"


def digest(data):
    return hashlib.sha256(data).hexdigest()


def verify_file(path, pin):
    with path.open("rb") as stream:
        if path.stat().st_size != pin["length"] or hashlib.file_digest(stream, "sha256").hexdigest() != pin["sha256"]:
            raise ValueError(f"Pinned input differs: {path.name}")


def catalog_rows(fonts, first):
    # Match XeTeX's catalog ABI. The second family name is a lookup alias.
    # The FSType, full name, PostScript name and font bytes are untouched.
    for index, font in enumerate(fonts, first):
        style = font["style"]
        weight, width, slant = font["weightWidthSlant"]
        regular, bold, italic = font["styleFlags"]
        yield "\n".join(map(str, [
            index, "/tex/" + font["name"], 0, 2, font["family"], font["alias"],
            1, style, 1, font["fullName"], font["postscript"], style, weight, width,
            slant, regular, bold, italic, 10, 0, 0, 0, 0,
        ])).encode("ascii") + b"\n"


def resources(old, lock):
    pins = {pin["name"]: pin for pin in json.loads(old.read("pins.json"))}
    for font in lock["fonts"]:
        pin = pins[font["name"]]
        data = old.read("files/" + pin["name"])
        if len(data) != pin["length"] or digest(data) != pin["sha256"]:
            raise ValueError(f"Pinned font differs: {pin['name']}")
    catalog = old.read("files/" + CATALOG)
    first = len(catalog.splitlines()) // 22 + 1
    if len(catalog.splitlines()) % 22:
        raise ValueError("Unexpected base catalog record layout")
    notice = lock["notice"]
    if notice["name"] in pins:
        raise ValueError("Notice overlaps base compiler")
    return pins, {
        CATALOG: catalog + b"".join(catalog_rows(lock["fonts"], first)),
        notice["name"]: notice["text"].encode("utf-8"),
    }


def assemble(base, out, lock):
    verify_file(base, lock["base"])
    with zipfile.ZipFile(base) as old:
        pins, added = resources(old, lock)
        for name, data in added.items():
            pins[name] = dict(name=name, length=len(data), sha256=digest(data))
        rows = sorted(pins.values(), key=lambda pin: pin["name"])
        closure = digest("\n".join(f"{p['name']}\t{p['length']}\t{p['sha256']}" for p in rows).encode())
        runtime = dict(count=len(rows), totalBytes=sum(p["length"] for p in rows), closure=closure,
                       identity=digest(f"native-runtime-profile-v1\n{lock['runtimeIdentityName']}\n{closure}".encode()))
        if runtime != lock["runtime"]:
            raise ValueError("Compatible font inventory differs from its lock")
        with out.open("xb") as target:
            try:
                with zipfile.ZipFile(target, "w", compression=zipfile.ZIP_STORED, allowZip64=False) as archive:
                    archive.writestr(zipfile.ZipInfo("pins.json"), (json.dumps(rows, indent=2) + "\n").encode())
                    for row in rows:
                        name = row["name"]
                        archive.writestr(zipfile.ZipInfo("files/" + name), added[name] if name in added else old.read("files/" + name))
                verify_file(out, lock["archive"])
            except Exception:
                out.unlink()
                raise
    return runtime


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()
    try:
        print(json.dumps(assemble(args.base, args.out, json.loads(LOCK.read_bytes()))))
    except (ValueError, KeyError, OSError, zipfile.BadZipFile) as error:
        parser.exit(2, f"Refused XeTeX compatibility archive: {error}\n")
