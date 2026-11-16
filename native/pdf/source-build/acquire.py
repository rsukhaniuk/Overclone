#!/usr/bin/env python3
"""Explicit, offline-core-independent acquisition of exact PDFium source inputs.

Run once into a new directory. No DEPS hooks, package managers, unpinned refs or
runtime downloads. The checked-in manifest fixes decoded sizes and SHA256.
"""
import base64
import hashlib
import json
from pathlib import Path
import sys
import tarfile
import urllib.request
import zipfile

manifest = json.loads(Path(__file__).with_name("inputs.json").read_text())
root = Path(sys.argv[1]).resolve()
if root.exists() and any(root.iterdir()):
    raise SystemExit("output must be a new empty directory")
root.mkdir(parents=True, exist_ok=True)
source = root / "source"
source.mkdir()
archives = root / "archives"
archives.mkdir()
if sum(i["bytes"] for i in manifest["inputs"]) > manifest["downloadCapBytes"]:
    raise SystemExit("declared source download cap exceeded")
working = 0
receipts = []
for index, item in enumerate(manifest["inputs"]):
    destination = (source / item["path"]).resolve()
    if not destination.is_relative_to(root):
        raise SystemExit("input destination escapes output")
    if not item["url"].startswith("https://"):
        raise SystemExit("non-HTTPS source")
    # Base64 is Gitiles' transport, not the corresponding source file format.
    wire_cap = item["bytes"] * 2 + 4096 if item["format"] == "base64" else item["bytes"]
    with urllib.request.urlopen(item["url"], timeout=120) as response:
        data = response.read(wire_cap + 1)
    if len(data) > wire_cap:
        raise SystemExit("source transport size mismatch")
    if item["format"] == "base64":
        data = base64.b64decode(data, validate=True)
    if len(data) != item["bytes"] or hashlib.sha256(data).hexdigest() != item["sha256"]:
        raise SystemExit("source digest/size mismatch")
    archive = archives / f"{index:02}.{item['format']}"
    archive.write_bytes(data)
    working += len(data)
    if item["format"] == "tar.gz":
        with tarfile.open(archive) as bundle:
            working += sum(m.size for m in bundle.getmembers() if m.isfile())
            if working > manifest["workingCapBytes"]:
                raise SystemExit("source working cap exceeded")
            destination.mkdir(parents=True, exist_ok=True)
            bundle.extractall(destination, filter="data")
    elif item["format"] == "zip":
        with zipfile.ZipFile(archive) as bundle:
            for member in bundle.infolist():
                if not (destination / member.filename).resolve().is_relative_to(destination):
                    raise SystemExit("tool archive escapes destination")
                working += member.file_size
            if working > manifest["workingCapBytes"]:
                raise SystemExit("source working cap exceeded")
            destination.mkdir(parents=True, exist_ok=True)
            bundle.extractall(destination)
    else:
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(data)
    receipts.append(item)
    (root / "acquisition-receipt.json").write_text(json.dumps({"schemaVersion": 1, "inputs": receipts}, indent=2) + "\n")
    print(item["path"], item["bytes"], item["sha256"], flush=True)
for tool in [root / "tools/gn/gn", root / "tools/ninja/ninja"]:
    tool.chmod(0o755)
