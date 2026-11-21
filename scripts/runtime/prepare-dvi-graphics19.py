#!/usr/bin/env python3
"""Assemble the pinned DVI graphics closure from the exact P18 and PSNFSS inputs."""
import argparse
import importlib.util
import io
import json
from pathlib import Path
import tarfile

HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("assembly", HERE / "prepare-biber-semantics.py")
COMMON = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(COMMON)

OLD_DRIVER = b"  \\def\\Gin@driver{dvips.def}%\n  \\ExecuteOptions{dvips}%"
NEW_DRIVER = b"  \\def\\Gin@driver{dvipdfmx.def}%\n  \\ExecuteOptions{dvipdfmx}%"
NOTICE = b"""PSNFSS times.sty, TeX Live 2023 final
The unchanged package file carries its original LaTeX Project Public License notice.
The exact archive, member and hashes are in dvi-graphics19.lock.json.
The DVI color configuration retains the upstream LPPL notice and selects
dvipdfmx for the DVI default. The converter includes the confined project
path lookup patch. Earlier engine and renderer resources retain their notices.
"""


def assemble(base, psnfss, converter, output):
    base = COMMON.safe_path(base, directory=True)
    psnfss = COMMON.safe_path(psnfss)
    converter = COMMON.safe_path(converter)
    output = COMMON.safe_path(output, fresh=True)
    if any(COMMON.overlap(output, source) for source in (base, psnfss, converter, HERE)):
        raise ValueError("Output overlaps an input")

    lock = json.loads((HERE / "dvi-graphics19.lock.json").read_bytes())
    if lock["schemaVersion"] != 1 or lock["profile"] != 19:
        raise ValueError("Unsupported recipe version")
    baseline, runtime = lock["base"], lock["runtime"]
    old = json.loads(COMMON.read_checked(base / "pins.json", baseline["pins"], 2 * 1024 * 1024))
    if len(old) != baseline["count"] or COMMON.closure(old) != baseline["closure"]:
        raise ValueError("Incomplete P18 baseline")
    color = COMMON.read_checked(base / "files" / "color.cfg", lock["dviColor"]["base"], 4096)
    if color.count(OLD_DRIVER) != 1:
        raise ValueError("Unexpected color driver configuration")

    archive = lock["psnfss"]["archive"]
    resource = lock["psnfss"]["resource"]
    with tarfile.open(fileobj=io.BytesIO(COMMON.read_checked(
            psnfss, {"length": archive["bytes"], "sha256": archive["sha256"]}, 64 * 1024)), mode="r:xz") as package:
        member = package.getmember(resource["archiveMember"])
        if not member.isfile() or member.size != resource["length"]:
            raise ValueError("Invalid PSNFSS member")
        times = package.extractfile(member).read(resource["length"] + 1)

    replacement = lock["replaced"][0]
    writes = {
        "times.sty": times,
        "dvi-graphics-color.cfg": color.replace(OLD_DRIVER, NEW_DRIVER),
        "notices/dvi-graphics19/package-provenance.txt": NOTICE,
        replacement["new"]["name"]: COMMON.read_checked(converter, replacement["new"], 2 * 1024 * 1024),
    }
    prior = {p["name"]: p for p in old}
    if prior.get(replacement["base"]["name"]) != replacement["base"]:
        raise ValueError("Unexpected parent DVI converter")
    rows = sorted([p for p in old if p["name"] != replacement["base"]["name"]]
                  + lock["added"] + [replacement["new"]], key=lambda row: row["name"])
    folded = {COMMON.resource_name(row["name"]).lower() for row in rows}
    manifest = (json.dumps(rows, indent=2) + "\n").encode()
    if (len(folded) != len(rows) or len(rows) != runtime["count"]
            or sum(p["length"] for p in rows) != runtime["totalBytes"]
            or COMMON.closure(rows) != runtime["closure"]
            or COMMON.digest(manifest) != runtime["pins"]["sha256"]):
        raise ValueError("Ambiguous or incomplete P19 inventory")

    materials = {}
    for row in rows:
        name = row["name"]
        data = writes.get(name)
        if data is None:
            data = COMMON.read_checked(base / "files" / name, row, 64 * 1024 * 1024)
        if len(data) != row["length"] or COMMON.digest(data) != row["sha256"]:
            raise ValueError("Derived resource hash mismatch: " + name)
        materials[name] = data
    output.mkdir()
    for name, data in materials.items():
        path = output / "files" / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
    (output / "pins.json").write_bytes(manifest)
    return runtime


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("base", "psnfss", "converter", "out"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    try:
        print(json.dumps(assemble(args.base, args.psnfss, args.converter, args.out)))
    except (ValueError, KeyError, TypeError, OSError, tarfile.TarError) as error:
        parser.exit(2, "Refused P19 runtime: " + str(error) + "\n")
