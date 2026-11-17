#!/usr/bin/env python3
"""Assemble exact P16 from P15, prepared free resources and two converters.

Explicit local inputs only: no downloads, builds, project files, default changes
or product acceptance. Earlier runtime inventories remain immutable.
"""
import argparse
import importlib.util
import json
from pathlib import Path

spec = importlib.util.spec_from_file_location("assembly", Path(__file__).with_name("prepare-biber-semantics.py"))
common = importlib.util.module_from_spec(spec)
spec.loader.exec_module(common)


def inventory(folder, expected, limits):
    raw = common.read_checked(folder / "pins.json", expected["pins"], limits["manifestBytes"])
    rows = json.loads(raw)
    if (not isinstance(rows, list) or len(rows) != expected["resourceCount"]
            or len(rows) > limits["resourceCount"]
            or any(not isinstance(row, dict) or set(row) != {"name", "length", "sha256"} for row in rows)):
        raise ValueError("Invalid exact inventory")
    names = [common.resource_name(row["name"]) for row in rows]
    if (names != sorted(set(names)) or common.closure(rows) != expected["closureSha256"]
            or sum(row["length"] for row in rows) != expected["totalBytes"]
            or expected["totalBytes"] > limits["totalBytes"]):
        raise ValueError("Mixed inventory or closure")
    return {row["name"]: (row, common.read_checked(folder / "files" / row["name"], row, limits["fileBytes"]))
            for row in rows}


def assemble(args):
    lock_bytes = common.safe_path(Path(__file__).with_name("input-budget16.lock.json")).read_bytes()
    lock = json.loads(lock_bytes)
    if lock["schemaVersion"] != 1 or lock["profile"] != 16:
        raise ValueError("Unsupported closed profile")
    inputs = {name: common.safe_path(getattr(args, name), directory=True)
              for name in ("base", "resources", "xe", "dvi")}
    out = common.safe_path(args.out, fresh=True)
    if any(common.overlap(out, path) for path in (*inputs.values(), Path(__file__).absolute().parent)):
        raise ValueError("Output overlaps an input")
    limits = lock["limits"]
    owned = inventory(inputs["base"], lock["base"], limits)
    resources = inventory(inputs["resources"], lock["resources"], limits)
    replacements = {row["candidate"]["name"]: row for row in lock["replacedResources"]}
    if len(replacements) != 1 or set(replacements) != {"xetexfontlist.txt"}:
        raise ValueError("Unexpected replacement declaration")
    for name, (row, data) in resources.items():
        if name in replacements:
            change = replacements.pop(name)
            if name not in owned or owned[name][0] != change["parent"] or row != change["candidate"]:
                raise ValueError("Mixed parent catalogue")
            if not data.startswith(owned[name][1]):
                raise ValueError("Existing font entries changed")
        elif name in owned:
            raise ValueError("Additive resource collision")
        owned[name] = (row, data)
    if replacements:
        raise ValueError("Missing replacement")
    changed = set()
    for component in lock["components"]:
        name = common.resource_name(component["name"])
        if name not in owned or name in changed or name in resources or component["group"] not in ("xe", "dvi"):
            raise ValueError("Invalid component replacement")
        changed.add(name)
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
    out.mkdir()
    for name, (_, data) in owned.items():
        file = out / "files" / name
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_bytes(data)
    (out / "pins.json").write_bytes(raw)
    receipt = {"schemaVersion": 1, "profile": 16, **target, "lockSha256": common.digest(lock_bytes),
               "phases": lock["phases"], "projectLimits": lock["projectLimits"],
               "scope": "Exact artifact assembly; no native build, default adoption or browser acceptance"}
    (out / "profile16-staging.json").write_text(json.dumps(receipt, indent=2) + "\n")
    return receipt


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("base", "resources", "xe", "dvi", "out"):
        parser.add_argument("--" + name, type=Path, required=True)
    try:
        print(json.dumps(assemble(parser.parse_args())))
    except (ValueError, OSError, KeyError, TypeError) as error:
        parser.exit(2, "Refused input-budget runtime: " + str(error) + "\n")
