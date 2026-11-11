#!/usr/bin/env python3
"""Reassemble closed P14 from exact P13, additive packages and native candidates.

Inputs are explicit local directories; this command does not download, build,
select a default or establish product acceptance. It preserves every old byte
except the five declared replacements. Microsoft fonts are not runtime inputs.
"""
import argparse
import importlib.util
import json
from pathlib import Path

spec = importlib.util.spec_from_file_location("biber_assembly", Path(__file__).with_name("prepare-biber-semantics.py"))
common = importlib.util.module_from_spec(spec)
spec.loader.exec_module(common)


def assemble(args):
    lock_path = Path(__file__).with_name("thesis-compatibility.lock.json")
    lock_bytes = common.safe_path(lock_path).read_bytes()
    lock = json.loads(lock_bytes)
    if lock["schemaVersion"] != 1 or lock["profile"] != 14:
        raise ValueError("Unsupported closed profile")
    inputs = {name: common.safe_path(getattr(args, name), directory=True)
              for name in ("base", "overlay", "xe", "lua", "lua_resource")}
    out = common.safe_path(args.out, fresh=True)
    if any(common.overlap(out, path) for path in (*inputs.values(), Path(__file__).absolute().parent)):
        raise ValueError("Output overlaps input")
    limits = lock["limits"]
    inventories = {}
    for group in ("base", "overlay"):
        raw = common.read_checked(inputs[group] / "pins.json", lock[group]["pins"], limits["manifestBytes"])
        rows = json.loads(raw)
        if (not isinstance(rows, list) or len(rows) != lock[group]["resourceCount"]
                or common.closure(rows) != lock[group]["closureSha256"]
                or sum(row["length"] for row in rows) != lock[group]["totalBytes"]):
            raise ValueError("Mixed resource inventory")
        names = [common.resource_name(row["name"]) for row in rows]
        if names != sorted(set(names)) or any(set(row) != {"name", "length", "sha256"} for row in rows):
            raise ValueError("Unsorted or duplicate resources")
        inventories[group] = rows
    common.read_checked(Path(__file__).parents[2] / "native/tex/thesis-packages/packages.lock.json",
                        lock["overlay"]["recipeLock"], limits["manifestBytes"])
    owned = {}
    for group, rows in inventories.items():
        for row in rows:
            name = row["name"]
            if name in owned:
                raise ValueError("Additive resource collision")
            owned[name] = (row, common.read_checked(inputs[group] / "files" / name, row, limits["fileBytes"]))
    replaced = set()
    for component in lock["components"]:
        name = common.resource_name(component["name"])
        if name in replaced or name not in owned:
            raise ValueError("Unexpected or duplicate replacement")
        replaced.add(name)
        data = common.read_checked(inputs[component["group"]] / common.resource_name(component["source"]),
                                   component, limits["fileBytes"])
        owned[name] = ({key: component[key] for key in ("name", "length", "sha256")}, data)
    rows = [owned[name][0] for name in sorted(owned)]
    raw = (json.dumps(rows, indent=2) + "\n").encode()
    target = lock["target"]
    if (len(rows) != target["resourceCount"] or len(rows) > limits["resourceCount"]
            or sum(row["length"] for row in rows) != target["totalBytes"]
            or target["totalBytes"] > limits["totalBytes"]
            or common.closure(rows) != target["closureSha256"]
            or len(raw) != target["pins"]["length"] or common.digest(raw) != target["pins"]["sha256"]):
        raise ValueError("Incomplete closed target")
    receipt = {"schemaVersion": 1, "profile": 14, **target,
               "lockSha256": common.digest(lock_bytes), "baseClosureSha256": lock["base"]["closureSha256"],
               "overlayClosureSha256": lock["overlay"]["closureSha256"],
               "phases": lock["phases"], "projectLimits": lock["projectLimits"],
               "scope": "Exact artifact assembly; no native build, default adoption or browser acceptance"}
    out.mkdir()
    for name, (_, data) in owned.items():
        path = out / "files" / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
    (out / "pins.json").write_bytes(raw)
    (out / "profile14-staging.json").write_text(json.dumps(receipt, indent=2) + "\n")
    return receipt


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("base", "overlay", "xe", "lua", "lua-resource", "out"):
        parser.add_argument("--" + name, type=Path, required=True)
    try:
        print(json.dumps(assemble(parser.parse_args())))
    except (ValueError, OSError, KeyError, TypeError) as error:
        parser.exit(2, "Refused thesis runtime: " + str(error) + "\n")
