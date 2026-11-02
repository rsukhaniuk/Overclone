#!/usr/bin/env python3
"""Assemble closed profile10 from verified, already acquired profile9 resources.

No download or default adoption. Original resources and formats stay immutable.
The application ZIP envelope is produced separately by package-compiler-runtime.py.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil

BASE_CLOSURE = "e82466563a5865fb5c400b3fbda82518f709a78ed40ae6d8289998e75fbeb9f7"
COMPONENTS = {
    "luatex.js": ("lua", "luatex.js", 193012, "6fb762c18ea4719dd2157d5950303a5f498ad511df0665cca54af141ff0882b5"),
    "luatex.wasm": ("lua", "luatex.wasm", 5198606, "220999882c2ddb4057b6d798f21785fce35accb4c63c0436dfa62af9f3987dbc"),
    "dvi-map-converter.js": ("dvi", "oc-dvi.js", 13026, "a961144a37546755175dfda0687c22bb79e67ebef495aa84c3922b01af6f2c57"),
    "dvi-map-converter.wasm": ("dvi", "oc-dvi.wasm", 691182, "53bd8a8a98c509afa8b0e0e5451bf78d7e9d765da1c8f513d9c1a909e9d5270d"),
    "xdv-map-converter.js": ("xe", "oc-dvi.js", 13026, "a961144a37546755175dfda0687c22bb79e67ebef495aa84c3922b01af6f2c57"),
    "xdv-map-converter.wasm": ("xe", "oc-dvi.wasm", 691189, "ca030fe4077fc02338e4d92cdc5c0ef1b89c73bcb629b9d44f59708993fa15ef"),
}


def digest(data):
    return hashlib.sha256(data).hexdigest()


def closure(pins):
    return digest("\n".join(f"{p['name']}\t{p['length']}\t{p['sha256']}" for p in pins).encode())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for flag in ("base", "lua", "dvi", "xe", "out"):
        parser.add_argument("--" + flag, type=Path, required=True)
    args = parser.parse_args()
    pins = json.loads((args.base / "pins.json").read_text())
    if not isinstance(pins, list) or len(pins) != 3709 or closure(pins) != BASE_CLOSURE:
        raise ValueError("Expected exact accepted profile9 inventory")
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
            raise ValueError("Changed profile9 resource: " + name)
        inputs[name] = (dict(pin), source)
    for name, (kind, filename, length, sha256) in COMPONENTS.items():
        source = getattr(args, kind) / filename
        raw = source.read_bytes()
        if len(raw) != length or digest(raw) != sha256:
            raise ValueError("Unreviewed graphics component: " + name)
        inputs[name] = ({"name": name, "length": length, "sha256": sha256}, source)
    updated = [inputs[name][0] for name in sorted(inputs)]
    args.out.mkdir()
    for name, (_, source) in inputs.items():
        target = args.out / "files" / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
    (args.out / "pins.json").write_text(json.dumps(updated, indent=2) + "\n")
    receipt = {"profile": 10, "resourceCount": len(updated),
               "totalBytes": sum(p["length"] for p in updated), "closureSha256": closure(updated),
               "baseClosureSha256": BASE_CLOSURE, "changedResources": list(COMPONENTS),
               "phases": {"pdflatex": 6, "latex-dvi": 15, "xelatex": 16, "lualatex": 17},
               "requiredCapabilities": {"lua": "oc_lua_graphics_abi()==1", "dvi": "oc_dvi_graphics_abi()==1", "xe": "oc_dvi_graphics_abi()==1"},
               "scope": "Closed candidate only; base ABI and every existing isolation/admission guard still required. No default adoption or redistribution approval."}
    (args.out / "profile10-staging.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print(json.dumps(receipt))


if __name__ == "__main__":
    main()
