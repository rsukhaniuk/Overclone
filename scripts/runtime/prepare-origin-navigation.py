#!/usr/bin/env python3
"""Assemble closed profile11 from verified, already acquired profile10 resources.

No download or default adoption. Original resources and formats stay immutable.
The application ZIP envelope is produced separately by package-compiler-runtime.py.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil

BASE_CLOSURE = "d91e3ff8d2c7717344d6c954efc8172ac0296223ece425df7ec00aff73db20cf"
COMPONENTS = {
    "luatex.js": ("lua", "luatex.js", 193122, "ffa8dc64e2e9cbdeba8f484778bce5dd2f3e476c8859f970e5bb3e85c9656fda"),
    "luatex.wasm": ("lua", "luatex.wasm", 5202871, "da6b84fc6741f8ed4aec6ae97d8895f1a16eb0a3d1a52aca1da8fd204ebc6107"),
    "dvi-map-converter.js": ("dvi", "oc-dvi.js", 13088, "8775bd9fb0d15d7f5933518d0728d792ae07d040332b4b257c7ae0b5d3741591"),
    "dvi-map-converter.wasm": ("dvi", "oc-dvi.wasm", 693723, "26cf002d26cacfa79f58e1c54fed61a1fba4ce5206b496088b1284b58aab9be2"),
    "xdv-map-converter.js": ("xe", "oc-dvi.js", 13088, "8775bd9fb0d15d7f5933518d0728d792ae07d040332b4b257c7ae0b5d3741591"),
    "xdv-map-converter.wasm": ("xe", "oc-dvi.wasm", 693727, "a9f15be70175334cf1db61df9580679116968b5afe3447310393db22b86ed385"),
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
            raise ValueError("Unreviewed origin component: " + name)
        inputs[name] = ({"name": name, "length": length, "sha256": sha256}, source)
    updated = [inputs[name][0] for name in sorted(inputs)]
    args.out.mkdir()
    for name, (_, source) in inputs.items():
        target = args.out / "files" / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
    (args.out / "pins.json").write_text(json.dumps(updated, indent=2) + "\n")
    receipt = {"profile": 11, "resourceCount": len(updated),
               "totalBytes": sum(p["length"] for p in updated), "closureSha256": closure(updated),
               "baseClosureSha256": BASE_CLOSURE, "changedResources": list(COMPONENTS),
               "phases": {"pdflatex": 6, "latex-dvi": 18, "xelatex": 19, "lualatex": 20},
               "requiredCapabilities": {"lua": ["oc_lua_graphics_abi()==1", "oc_lua_origin_abi()==1"], "dvi": ["oc_dvi_graphics_abi()==1", "oc_dvi_origin_abi()==1"], "xe": ["oc_dvi_graphics_abi()==1", "oc_dvi_origin_abi()==1"]},
               "scope": "Closed candidate only; base ABI and every existing isolation/admission guard still required. No default adoption or redistribution approval."}
    (args.out / "profile11-staging.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print(json.dumps(receipt))


if __name__ == "__main__":
    main()
