#!/usr/bin/env python3
"""Assemble the exact LuaLaTeX coursework resource closure from pinned inputs."""
import argparse
import importlib.util
import json
from pathlib import Path


HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("assembly", HERE / "prepare-biber-semantics.py")
COMMON = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(COMMON)


def assemble(base, supplements, output):
    base = COMMON.safe_path(base, directory=True)
    supplements = COMMON.safe_path(supplements, directory=True)
    output = COMMON.safe_path(output, fresh=True)
    if any(COMMON.overlap(output, source) for source in (base, supplements, HERE)):
        raise ValueError("Output overlaps an input")

    lock = json.loads((HERE / "coursework18.lock.json").read_bytes())
    if lock["schemaVersion"] != 1 or lock["profile"] != 18:
        raise ValueError("Unsupported recipe version")
    baseline, runtime = lock["baseline"], lock["runtime"]
    prior = json.loads(COMMON.read_checked(base / "pins.json", baseline["pins"], 2 * 1024 * 1024))
    if len(prior) != baseline["count"] or COMMON.closure(prior) != baseline["closure"]:
        raise ValueError("Incomplete P17 baseline")

    added = lock["addedCompilerResources"] + [notice["resource"] for notice in lock["notices"]]
    prior_names = {row["name"] for row in prior}
    rows = sorted([*prior, *added], key=lambda row: row["name"])
    folded = {COMMON.resource_name(row["name"]).lower() for row in rows}
    manifest = (json.dumps(rows, indent=2) + "\n").encode()
    if len(folded) != len(rows) or len(rows) != runtime["count"] \
            or sum(row["length"] for row in rows) != runtime["totalBytes"] \
            or COMMON.closure(rows) != runtime["closure"] or COMMON.digest(manifest) != runtime["pinsSha256"]:
        raise ValueError("Ambiguous or incomplete P18 inventory")

    materials = {}
    for row in rows:
        source = base / "files" if row["name"] in prior_names else supplements
        materials[row["name"]] = COMMON.read_checked(source / row["name"], row, 64 * 1024 * 1024)
    output.mkdir()
    for name, data in materials.items():
        path = output / "files" / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
    (output / "pins.json").write_bytes(manifest)
    return runtime


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("base", "supplements", "out"):
        parser.add_argument("--" + name, type=Path, required=True)
    try:
        args = parser.parse_args()
        print(json.dumps(assemble(args.base, args.supplements, args.out)))
    except (ValueError, KeyError, TypeError, OSError) as error:
        parser.exit(2, "Refused P18 runtime: " + str(error) + "\n")
