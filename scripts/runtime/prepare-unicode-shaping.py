#!/usr/bin/env python3
"""Assemble closed profile12 from verified, already acquired profile10 resources.

No download or default adoption. Original resources and formats stay immutable.
The application ZIP envelope is produced separately by package-compiler-runtime.py.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil

BASE_CLOSURE = "d91e3ff8d2c7717344d6c954efc8172ac0296223ece425df7ec00aff73db20cf"
FONT_MANIFEST_HASH = "14b9c674f93ead72f877814a311839fa3dedd3e8a8940d8c54e2a2b380f5fe4f"
COMPONENTS = {
    'xetex.js': ('xe', 'engine.js', 69948, '38ba92890c5de6e957254dcda1c1345a75b35dabaa46d77fc4835f3ad4f33601'),
    'xetex.wasm': ('xe', 'engine.wasm', 3302272, '5a08c4d0a2109adc43e087889be208deb71cf4ba55692b41d58da2c8284f1e8e'),
    'luatex.js': ('lua', 'luatex.js', 193208, 'b66ac597a51b06a97598c95db54329005db54ff5631e461e36f1a841bec86d90'),
    'luatex.wasm': ('lua', 'luatex.wasm', 6013318, '7122d44f4d63a4626a3bcc55bb6bb404cfb8d78fd5419dda38849e991e23b7c6'),
    'lualatex.fmt': ('format', 'lualatex.fmt', 12437864, 'fed204915ab2eb5b593ee232ae2a484d58e2dc39737b902aefbaf123a2d14e02'),
    'harfbuzz.lua': ('harfbuzz', 'harfbuzz.lua', 3023, '6afe2357e6d96c656a903965010ebb5e8b11a40ca73cdc9c481cd7d44e56b47d'),
}


def digest(data):
    return hashlib.sha256(data).hexdigest()


def closure(pins):
    return digest("\n".join(f"{p['name']}\t{p['length']}\t{p['sha256']}" for p in pins).encode())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for flag in ("base", "lua", "xe", "format", "harfbuzz", "fonts", "out"):
        parser.add_argument("--" + flag, type=Path, required=True)
    args = parser.parse_args()
    pins = json.loads((args.base / "pins.json").read_text())
    if not isinstance(pins, list) or len(pins) != 3709 or closure(pins) != BASE_CLOSURE:
        raise ValueError("Expected exact accepted profile10 inventory")
    if args.out.exists():
        raise ValueError("Output must be a new directory; accepted candidates are immutable")
    inputs = {}
    for pin in pins:
        name = pin["name"]
        if not name or name.startswith("/") or "\\" in name or any(p in ("", ".", "..") for p in name.split("/")):
            raise ValueError("Invalid resource name")
        source = args.base / "files" / name
        raw = source.read_bytes()
        if len(raw) != pin["length"] or digest(raw) != pin["sha256"]:
            raise ValueError("Changed profile10 resource: " + name)
        inputs[name] = (dict(pin), source)
    for name, (kind, filename, length, sha256) in COMPONENTS.items():
        source = getattr(args, kind) / filename
        raw = source.read_bytes()
        if len(raw) != length or digest(raw) != sha256:
            raise ValueError("Unreviewed Unicode/shaping component: " + name)
        inputs[name] = ({"name": name, "length": length, "sha256": sha256}, source)
    font_manifest = (args.fonts / "manifest.json").read_bytes()
    if digest(font_manifest) != FONT_MANIFEST_HASH:
        raise ValueError("Unreviewed generated-metric/outline manifest")
    fonts = json.loads(font_manifest)
    if len(fonts["files"]) != 476 or fonts["totalAddedBytes"] != 35622744:
        raise ValueError("Incomplete declared font closure")
    for font in fonts["files"]:
        name = font["name"]
        if name in inputs or "/" in name or "\\" in name or name.startswith("."):
            raise ValueError("Font name collision or path")
        source = args.fonts / "files" / name
        raw = source.read_bytes()
        if source.is_symlink() or len(raw) != font["length"] or digest(raw) != font["sha256"]:
            raise ValueError("Changed reviewed font: " + name)
        inputs[name] = ({"name": name, "length": font["length"], "sha256": font["sha256"]}, source)
    updated = [inputs[name][0] for name in sorted(inputs)]
    args.out.mkdir()
    for name, (_, source) in inputs.items():
        target = args.out / "files" / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
    (args.out / "pins.json").write_text(json.dumps(updated, indent=2) + "\n")
    receipt = {"profile": 12, "resourceCount": len(updated),
               "totalBytes": sum(p["length"] for p in updated), "closureSha256": closure(updated),
               "baseClosureSha256": BASE_CLOSURE, "changedResources": list(COMPONENTS),
               "fontAdditions": {"count": 476, "bytes": 35622744, "manifestSha256": FONT_MANIFEST_HASH},
               "phases": {"pdflatex": 6, "latex-dvi": 15, "xelatex": 21, "lualatex": 22},
               "requiredCapabilities": {"lua": "ABI5, graphics1, harfbuzz1, font_closure1", "xe": "oc_xe_nonbmp_abi()==1", "converters": "accepted P10 graphics1 unchanged"},
               "scope": "Closed candidate only; base ABI and every existing isolation/admission guard still required. No default adoption or redistribution approval."}
    (args.out / "profile12-staging.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print(json.dumps(receipt))


if __name__ == "__main__":
    main()
