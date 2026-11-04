#!/usr/bin/env python3
"""Verify acquired bytes and safely unpack the explicit approved LH inputs.

Private staging only. Reject links, special files, traversal and expansion bombs.
"""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import stat
import tarfile
import zipfile


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--acquired", type=Path, required=True)
    p.add_argument("--output", type=Path, required=True)
    a = p.parse_args()
    if a.output.exists():
        p.error("fresh extraction directory required")
    a.output.mkdir(parents=True)
    receipt = json.loads((a.acquired / "acquisition.json").read_text())
    files = {row["name"]: row for row in receipt["files"] if "error" not in row}
    budget = 96 * 1024 ** 2
    total = 0
    rows = []

    def name(raw):
        q = PurePosixPath(raw)
        if not raw or q.is_absolute() or ".." in q.parts or "\\" in raw or len(raw.encode()) > 512:
            raise ValueError("invalid archive path")
        return q

    def save(root, raw, size, stream):
        nonlocal total
        q = name(raw)
        if size < 0 or size > 8 * 1024 ** 2 or total + size > budget or len(rows) >= 20000:
            raise ValueError("archive expansion budget exceeded")
        target = root / q
        if target.exists():
            raise ValueError("duplicate archive member")
        target.parent.mkdir(parents=True, exist_ok=True)
        data = stream.read(size + 1)
        if len(data) != size:
            raise ValueError("invalid archive size")
        target.write_bytes(data)
        total += size
        rows.append({"archive": root.name, "member": str(q), "length": size, "sha256": hashlib.sha256(data).hexdigest()})

    for archive_name in ("lh.r15878.tar.xz", "lh-current.tar.xz", "lhfnt35g-source.zip", "lhfnt35g-gen-mf.zip"):
        source = a.acquired / "downloads" / archive_name
        pin = files[archive_name]
        data = source.read_bytes()
        assert len(data) == pin["length"] and hashlib.sha256(data).hexdigest() == pin["sha256"]
        root = a.output / archive_name
        root.mkdir()
        if archive_name.endswith(".zip"):
            with zipfile.ZipFile(source) as archive:
                for member in archive.infolist():
                    name(member.filename)
                    if member.is_dir():
                        continue
                    mode = member.external_attr >> 16
                    if stat.S_ISLNK(mode) or member.flag_bits & 1:
                        raise ValueError("link/encrypted archive member refused")
                    with archive.open(member) as stream:
                        save(root, member.filename, member.file_size, stream)
        else:
            with tarfile.open(source, "r|xz") as archive:
                for member in archive:
                    name(member.name)
                    if member.isdir():
                        continue
                    if not member.isfile():
                        raise ValueError("non-regular tar member refused")
                    with archive.extractfile(member) as stream:
                        save(root, member.name, member.size, stream)
    (a.output / "extraction.json").write_text(json.dumps({"schemaVersion": 1, "expandedBytes": total, "expandedCapBytes": budget, "files": rows}, indent=2) + "\n")
    print(json.dumps({"extractedFiles": len(rows), "expandedBytes": total}))


if __name__ == "__main__":
    main()
