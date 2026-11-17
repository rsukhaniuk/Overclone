#!/usr/bin/env python3
"""Stage exact historic authoring resources and a real-name font catalogue.

No downloads, project source, engine rebuilds or product default changes.
"""
import argparse
import hashlib
import json
from pathlib import Path
import stat
import subprocess
import tarfile


def digest(data):
    return hashlib.sha256(data).hexdigest()


def regular(path):
    if any(p.is_symlink() for p in (path, *path.parents)) or not stat.S_ISREG(path.stat().st_mode):
        raise ValueError("Expected a regular non-symlink input")
    return path.read_bytes()


def checked(path, pin):
    data = regular(path)
    if len(data) != pin["length"] or digest(data) != pin["sha256"]:
        raise ValueError("Input integrity mismatch: " + pin.get("name", path.name))
    return data


def safe(name):
    return not name.startswith("/") and all(p not in ("", ".", "..") for p in name.split("/"))


def prepare(archives, base, tool, out):
    recipe = Path(__file__).parent
    lock_bytes = regular(recipe / "resources.lock.json")
    lock = json.loads(lock_bytes)
    if lock["schemaVersion"] != 1:
        raise ValueError("Unsupported resource lock")
    original = regular(base / "pins.json")
    if len(original) != lock["base"]["pinsLength"] or digest(original) != lock["base"]["pinsSha256"]:
        raise ValueError("Expected immutable P15 inventory")
    existing = {p["name"]: p for p in json.loads(original)}
    targets = {}
    by_archive = {}
    for row in lock["resources"]:
        name = row["name"]
        if not safe(name) or name in existing or name in targets:
            raise ValueError("Unsafe or colliding additive resource")
        targets[name] = None
        members = by_archive.setdefault(row["archive"], {})
        if row["member"] in members:
            raise ValueError("Duplicate locked member")
        members[row["member"]] = row
    compressed = decoded = count = 0
    for archive in lock["archives"]:
        name = archive["name"]
        if Path(name).name != name:
            raise ValueError("Unsafe archive filename")
        data = checked(archives / name, archive)
        if hashlib.sha512(data).hexdigest() != archive["sha512"]:
            raise ValueError("Historic inventory SHA512 mismatch")
        compressed += len(data)
        if compressed > lock["limits"]["compressedArchiveBytes"]:
            raise ValueError("Compressed archive budget exceeded")
        declared = by_archive.get(name, {})
        seen = set()
        with tarfile.open(archives / name, "r:xz") as source:
            for member in source:
                count += 1
                if count > lock["limits"]["archiveMembers"] or not safe(member.name):
                    raise ValueError("Unsafe or excessive archive members")
                if member.isfile():
                    decoded += member.size
                    if decoded > lock["limits"]["decodedArchiveBytes"]:
                        raise ValueError("Decoded archive budget exceeded")
                if member.name not in declared:
                    continue
                row = declared[member.name]
                if member.name in seen or not member.isfile() or member.size != row["length"]:
                    raise ValueError("Invalid locked archive member")
                seen.add(member.name)
                payload = source.extractfile(member).read(row["length"] + 1)
                if len(payload) != row["length"] or digest(payload) != row["sha256"]:
                    raise ValueError("Member integrity mismatch")
                targets[row["name"]] = payload
        if seen != set(declared):
            raise ValueError("Missing locked archive member")
    expected = lock["expected"]
    if (any(b is None for b in targets.values()) or len(targets) != expected["additiveResources"]
            or sum(map(len, targets.values())) != expected["additiveBytes"]
            or compressed != expected["compressedBytes"] or decoded != expected["decodedBytes"]):
        raise ValueError("Incomplete resource or source archive closure")
    contract = lock["catalogue"]
    parent = existing.get(contract["file"])
    if parent is None or any(parent[k] != contract["parent"][k] for k in ("length", "sha256")):
        raise ValueError("Unexpected parent font catalogue")
    checked(base / "files" / contract["file"], contract["parent"])
    for pin in contract["extractorComponents"]:
        checked(tool.parent / pin["name"], pin)
    if (out.exists() or not out.parent.is_dir()
            or any(p.is_symlink() for p in (out, *out.parents))
            or any(p == out or out in p.parents or p in out.parents
                   for p in (archives.resolve(), base.resolve(), tool.resolve()))):
        raise ValueError("Output must be fresh, separate and non-symlink")
    out.mkdir()
    for name, payload in targets.items():
        target = out / "files" / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(payload)
    subprocess.run(["node", str(recipe / "catalog.cjs"), str(tool), str(base), str(out),
                    str(out / "files" / contract["file"])], check=True, timeout=60)
    checked(out / "files" / contract["file"], contract["candidate"])
    pins = [{k: row[k] for k in ("name", "length", "sha256")} for row in lock["resources"]]
    pins.append({"name": contract["file"], **contract["candidate"]})
    pins.sort(key=lambda p: p["name"])
    closure = digest("\n".join(f"{p['name']}\t{p['length']}\t{p['sha256']}" for p in pins).encode())
    (out / "pins.json").write_text(json.dumps(pins, indent=2) + "\n")
    receipt = {"schemaVersion": 1, "additiveResources": len(targets),
               "additiveBytes": expected["additiveBytes"], "replacementResources": 1,
               "resourceCount": len(pins), "totalBytes": sum(p["length"] for p in pins),
               "closureSha256": closure, "lockSha256": digest(lock_bytes),
               "archivesVerified": len(lock["archives"]), "compressedBytes": compressed,
               "decodedBytes": decoded, "original77FontsByteExact": True, "fontAliases": 0,
               "baseUnchanged": regular(base / "pins.json") == original,
               "scope": "Resource preparation only; runtime closure and product acceptance are separate"}
    (out / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
    return receipt


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("archives", "base", "catalog-tool", "out"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    try:
        print(json.dumps(prepare(args.archives.resolve(), args.base.resolve(),
                                 args.catalog_tool.resolve(), args.out.absolute())))
    except (ValueError, OSError, KeyError, TypeError, tarfile.TarError,
            subprocess.CalledProcessError, subprocess.TimeoutExpired) as error:
        parser.exit(2, "Refused authoring resources: " + str(error) + "\n")
