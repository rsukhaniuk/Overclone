#!/usr/bin/env python3
"""Build genuine browser Biber from hash-locked archives without downloads.

Execute inside the separately locked bootstrap image with --network none,
2 CPUs, 1 GiB memory and a private writable output. Never use a shared SDK cache.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import posixpath
import shutil
import subprocess
import tarfile
import time


def unpack(archive, destination, row):
    data = archive.read_bytes()
    if len(data) != row["length"] or hashlib.sha256(data).hexdigest() != row["sha256"]:
        raise ValueError("source archive integrity mismatch: "+row["name"])
    with tarfile.open(archive, "r:*") as source:
        members = source.getmembers()
        if len(members) != row["members"] or len(members) > 50000:
            raise ValueError("source member count mismatch")
        roots, table, seen, expanded = set(), {}, set(), 0
        for member in members:
            name = member.name
            normalized = posixpath.normpath(name)
            if (not name or name.startswith("/") or ".." in name.split("/") or "\\" in name
                    or normalized in seen or member.mode & 0o6000
                    or not (member.isfile() or member.isdir() or member.islnk())):
                raise ValueError("unsafe source archive member")
            roots.add(name.split("/")[0]); table[name] = member; seen.add(normalized)
            expanded += member.size
        if len(roots) != 1:
            raise ValueError("ambiguous source root")
        for member in members:
            if member.islnk():
                target = table.get(member.linkname)
                if target is None or not target.isfile() or member.linkname.split("/")[0] not in roots:
                    raise ValueError("unsafe source hardlink")
                expanded += target.size
        if expanded != row["expandedBytes"] or expanded > 268435456:
            raise ValueError("source expanded size mismatch")
        destination.mkdir(parents=True)
        for member in members:
            parts = member.name.split("/")[1:]
            if not parts or parts == [""]:
                continue
            target = destination.joinpath(*parts)
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                original = table[member.linkname] if member.islnk() else member
                with source.extractfile(original) as content:
                    target.write_bytes(content.read())
                target.chmod(0o755 if member.mode & 0o111 else 0o644)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archives", type=Path, required=True, help="NAME.archive files from source-lock.json")
    parser.add_argument("--output", type=Path, required=True, help="fresh build tree; mount as /build for stable paths")
    args = parser.parse_args()
    root, component = args.output.resolve(), Path(__file__).resolve().parent
    if root.exists() and any(root.iterdir()):
        raise SystemExit("fresh empty output directory required")
    root.mkdir(parents=True, exist_ok=True)
    start = time.monotonic()
    lock = json.loads((component/"source-lock.json").read_text())
    for row in lock["sources"]:
        unpack(args.archives/(row["name"]+".archive"), root/"src"/row["name"], row)
    upstream = component/"upstream/texlyre-biber"
    provenance = json.loads((upstream/"UPSTREAM.json").read_text())
    # Unchanged upstream sources carry their own complete hash inventory.
    for row in provenance["files"]:
        path = upstream/row["path"]
        if hashlib.sha256(path.read_bytes()).hexdigest() != row["sha256"]:
            raise SystemExit("upstream port source integrity mismatch")
    shutil.copytree(upstream/"biber", root/"port")
    overlays = [
        ("build_biber.sh", "--use-preload-cache", "-O2 -sMAXIMUM_MEMORY=536870912 -Wl,--threads=1"),
        ("patches/06-hints-emscripten.patch", "Unicode/Normalize WebPerl $EMPERL_STATIC_EXT", "Unicode/Normalize $EMPERL_STATIC_EXT"),
    ]
    for name, before, after in overlays:
        path = root/"port"/name; text = path.read_text()
        if text.count(before) != 1:
            raise SystemExit("unexpected upstream source overlay preimage")
        path.write_text(text.replace(before, after))
    shutil.copytree("/emsdk/upstream/emscripten/cache", root/"cache")
    env = dict(os.environ, BUILD=str(root), PERL_VERSION="5.38.2", BIBER_VERSION="2.19",
               PERL_WASM_PREFIX="/work/runtime/perl", AR="emar", MAKEFLAGS="-j2",
               EM_CACHE=str(root/"cache"), EM_FROZEN_CACHE="1", EMCC_CORES="1",
               PATH="/emsdk/upstream/bin:"+os.environ["PATH"], TZ="UTC")
    with (root/"baseline.log").open("wb") as log:
        subprocess.run(["bash", str(root/"port/build_biber.sh")], env=env, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=1800)
    with (root/"link.log").open("wb") as log:
        subprocess.run(["python3", str(component/"link.py"), "--build-root", str(root), "--out", str(root/"component")], env=env, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=300)
    receipt = {"schemaVersion": 1, "status": "genuine-source-build-complete-not-runtime-acceptance",
               "elapsedSeconds": time.monotonic()-start, "networkDuringBuild": False,
               "sourceLockSha256": hashlib.sha256((component/"source-lock.json").read_bytes()).hexdigest(),
               "bootstrapLockSha256": hashlib.sha256((component/"bootstrap-lock.json").read_bytes()).hexdigest(),
               "upstreamCommit": "46ebb6216a5c1fe63dd1dc885831d1a8cef2dc86", "overlays": overlays}
    (root/"build-receipt.json").write_text(json.dumps(receipt, indent=2)+"\n")
    print(json.dumps(receipt))


if __name__ == "__main__":
    main()
