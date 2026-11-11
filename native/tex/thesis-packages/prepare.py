#!/usr/bin/env python3
"""Stage an additive, hash-locked TeX Live 2023 overlay from explicit archives.

Obtain the exact archives in packages.lock.json separately. Source and document
archives are verified alongside runtime archives; only declared members become
runtime resources. This command performs no download or default adoption.
"""
import argparse
import hashlib
import json
from pathlib import Path
import stat
import tarfile


def digest(data):
    return hashlib.sha256(data).hexdigest()


def regular(path):
    if any(p.is_symlink() for p in (path, *path.parents)) or not stat.S_ISREG(path.stat().st_mode):
        raise ValueError("Expected regular, non-symlink input")
    return path.read_bytes()


def prepare(archives, base, out):
    lock_bytes = regular(Path(__file__).with_name("packages.lock.json"))
    lock = json.loads(lock_bytes)
    if lock["schemaVersion"] != 1:
        raise ValueError("Unsupported overlay lock")
    original = regular(base / "pins.json")
    if len(original) != lock["base"]["pinsLength"] or digest(original) != lock["base"]["pinsSha256"]:
        raise ValueError("Expected immutable P13 inventory")
    accepted = {p["name"] for p in json.loads(original)}
    owned = {}
    members = {}
    for row in lock["resources"]:
        name = row["name"]
        if (name in accepted or name in owned or name.startswith("/")
                or any(p in ("", ".", "..") for p in name.split("/"))):
            raise ValueError("Resource collision or unsafe target")
        owned[name] = None
        members.setdefault(row["archive"], {})[row["member"]] = row
    for archive in lock["archives"]:
        name = archive["name"]
        if Path(name).name != name:
            raise ValueError("Unsafe archive name")
        data = regular(archives / name)
        if len(data) != archive["length"] or digest(data) != archive["sha256"]:
            raise ValueError("Archive integrity mismatch: " + name)
        declared = members.get(name, {})
        seen = set()
        with tarfile.open(archives / name, "r:xz") as source:
            for member in source:
                if member.name not in declared:
                    continue
                row = declared[member.name]
                if member.name in seen or not member.isfile() or member.size != row["length"]:
                    raise ValueError("Invalid or duplicate declared archive member")
                seen.add(member.name)
                stream = source.extractfile(member)
                payload = stream.read(row["length"] + 1)
                if len(payload) != row["length"] or digest(payload) != row["sha256"]:
                    raise ValueError("Member integrity mismatch")
                owned[row["name"]] = payload
        if seen != set(declared):
            raise ValueError("Missing declared archive member")
    if any(value is None for value in owned.values()):
        raise ValueError("Incomplete resource closure")
    if (out.exists() or not out.parent.is_dir()
            or any(p.is_symlink() for p in (out, *out.parents))
            or any(path == out or out in path.parents or path in out.parents
                   for path in (archives.resolve(), base.resolve()))):
        raise ValueError("Output must be fresh, separate and non-symlink")
    pins = [{k: row[k] for k in ("name", "length", "sha256")} for row in lock["resources"]]
    closure = digest("\n".join(f"{p['name']}\t{p['length']}\t{p['sha256']}" for p in pins).encode())
    out.mkdir()
    for name, payload in owned.items():
        target = out / "files" / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(payload)
    (out / "pins.json").write_text(json.dumps(pins, indent=2) + "\n")
    receipt = {"schemaVersion": 1, "resourceCount": len(pins),
               "totalBytes": sum(p["length"] for p in pins), "closureSha256": closure,
               "lockSha256": digest(lock_bytes), "archivesVerified": len(lock["archives"]),
               "baseUnchanged": digest(regular(base / "pins.json")) == lock["base"]["pinsSha256"],
               "scope": "Original additive resource staging only; no engine, font or product acceptance"}
    (out / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
    return receipt


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("archives", "base", "out"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    try:
        print(json.dumps(prepare(args.archives, args.base, args.out)))
    except (ValueError, OSError, KeyError, TypeError, tarfile.TarError) as error:
        parser.exit(2, "Refused thesis overlay: " + str(error) + "\n")
