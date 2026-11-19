#!/usr/bin/env python3
"""Assemble the exact opt-in P17 resource inventory from prepared local inputs."""
import argparse
import importlib.util
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("assembly", HERE / "prepare-biber-semantics.py")
common = importlib.util.module_from_spec(spec)
spec.loader.exec_module(common)


def assemble(args):
    baseline = common.safe_path(args.base, directory=True)
    supplement = common.safe_path(args.supplements, directory=True)
    worker = common.safe_path(args.worker)
    destination = common.safe_path(args.out, fresh=True)
    if any(common.overlap(destination, path) for path in (baseline, supplement, worker, HERE)):
        raise ValueError("Output overlaps an input")

    lock_bytes = common.safe_path(HERE / "selected-pdf17.lock.json").read_bytes()
    lock = json.loads(lock_bytes)
    if lock["schemaVersion"] != 1 or lock["candidateProfile"] != 17:
        raise ValueError("Unsupported recipe version")
    base, target = lock["P16"], lock["target"]
    base_manifest = common.read_checked(baseline / "pins.json", base["manifest"], 2 * 1024 * 1024)
    old = json.loads(base_manifest)
    if (len(old) != base["count"] or common.closure(old) != base["closure"]
            or sum(row["length"] for row in old) != base["totalBytes"]):
        raise ValueError("Incomplete P16 base")

    additions = lock["supplements"]
    rows = sorted([*old, *additions], key=lambda row: row["name"])
    names = {common.resource_name(row["name"]).lower() for row in rows}
    payload = (json.dumps(rows, indent=2) + "\n").encode()
    if (len(names) != len(rows) or len(rows) != target["count"]
            or sum(row["length"] for row in rows) != target["totalBytes"]
            or common.closure(rows) != target["closure"]
            or len(payload) != target["pins"]["length"] or common.digest(payload) != target["pins"]["sha256"]):
        raise ValueError("Ambiguous or incomplete P17 inventory")

    added_names = {row["name"] for row in additions}
    materials = {
        row["name"]: common.read_checked(
            (supplement if row["name"] in added_names else baseline / "files") / row["name"],
            row, 64 * 1024 * 1024)
        for row in rows
    }
    new_worker = common.read_checked(worker, lock["worker"], 36 * 1024)
    destination.mkdir()
    for name, data in materials.items():
        file = destination / "files" / name
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_bytes(data)
    (destination / "pins.json").write_bytes(payload)
    (destination / "compile-port-worker.js").write_bytes(new_worker)
    receipt = {
        "schemaVersion": 1, "candidateProfile": 17, "requiresExplicitSelection": True, "P16Preserved": True,
        "scriptSha256": common.digest(new_worker), "pinsSha256": common.digest(payload),
        "lockSha256": common.digest(lock_bytes), "bundle": target, "limits": lock["limits"],
    }
    (destination / "assembly.json").write_text(json.dumps(receipt, indent=2) + "\n")
    return receipt


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("base", "supplements", "worker", "out"):
        parser.add_argument("--" + name, type=Path, required=True)
    try:
        print(json.dumps(assemble(parser.parse_args())))
    except (ValueError, OSError, KeyError, TypeError) as error:
        parser.exit(2, "Refused P17 runtime: " + str(error) + "\n")
