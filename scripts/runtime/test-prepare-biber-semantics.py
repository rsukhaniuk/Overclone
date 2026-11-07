#!/usr/bin/env python3
"""Focused refusals against real locked inputs; creates only tiny bad fixtures.

Accepts the same component input flags as prepare-biber-semantics.py. --scratch
must be an existing writable private directory. Runtime inputs are never copied
or modified. Run in the same network-disabled Python container as the assembler.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    flags = ("base", "biber", "semantic", "pdf-format", "dvi-format", "converter", "lua", "lua-resource", "notices")
    for flag in flags:
        parser.add_argument("--"+flag, type=Path, required=True)
    parser.add_argument("--scratch", type=Path, required=True)
    args = parser.parse_args()
    tool = Path(__file__).with_name("prepare-biber-semantics.py")
    lock_path = tool.with_name("biber-semantics.lock.json")
    lock = json.loads(lock_path.read_text())
    original = {flag: str(getattr(args, flag.replace("-", "_"))) for flag in flags}
    results = []
    with tempfile.TemporaryDirectory(prefix="biber-assembly-refusal-", dir=args.scratch) as name:
        scratch = Path(name)
        component = scratch/"changed-resource.lua"; component.write_bytes(b"changed")
        notice_root = scratch/"notice-root"; notice = notice_root/lock["notices"][0]["source"]
        notice.parent.mkdir(parents=True); notice.write_bytes(b"changed")
        base = scratch/"mixed-base"; base.mkdir()
        rows = json.loads((args.base/"pins.json").read_text()); rows[0]["sha256"] = "0"*64
        (base/"pins.json").write_text(json.dumps(rows, indent=2)+"\n")
        link = scratch/"linked-resource.lua"; link.symlink_to(args.lua_resource.absolute())
        existing = scratch/"existing-output"; existing.mkdir(); sentinel = existing/"keep"; sentinel.write_bytes(b"retain output")
        review = scratch/"existing-pins.cs"; review.write_bytes(b"retain review source")
        cases = [
            ("changed-component", {"lua-resource": str(component)}, "length"),
            ("changed-notice", {"notices": str(notice_root)}, "length"),
            ("mixed-base", {"base": str(base)}, "integrity"),
            ("output-overlap", {"out": str(args.base/("must-not-create-"+scratch.name))}, "overlap"),
            ("input-symlink", {"lua-resource": str(link)}, "Symlink"),
            ("explicit-traversal", {"lua-resource": str(scratch/".."/scratch.name/component.name)}, "Traversal"),
            ("existing-output", {"out": str(existing)}, "fresh"),
            ("existing-review-source", {"pins-output": str(review)}, "fresh"),
        ]
        for label, changes, reason in cases:
            values = {**original, "out": str(scratch/(label+"-out")), "pins-output": str(scratch/(label+"-review.cs")), **changes}
            output, pins_output = Path(values["out"]), Path(values["pins-output"])
            existed, pins_existed = output.exists(), pins_output.exists()
            command = [sys.executable, str(tool)]
            for key, value in values.items():
                command.extend(["--"+key, value])
            result = subprocess.run(command, capture_output=True, text=True, timeout=60)
            if result.returncode != 2 or reason not in result.stderr:
                raise AssertionError(f"Unexpected refusal for {label}: {result.returncode}: {result.stderr}")
            if output.exists() != existed or pins_output.exists() != pins_existed:
                raise AssertionError("Validation wrote a target before refusing: "+label)
            if sentinel.read_bytes() != b"retain output" or review.read_bytes() != b"retain review source":
                raise AssertionError("Existing output/review source changed")
            results.append({"case": label, "status": "pass", "refusal": result.stderr.strip(), "targetWritten": False})
        print(json.dumps({"schemaVersion": 1, "status": "pass", "cases": results,
                          "assemblerSha256": hashlib.sha256(tool.read_bytes()).hexdigest(),
                          "lockSha256": hashlib.sha256(lock_path.read_bytes()).hexdigest(),
                          "scope": "Assembly validation only; no native rebuild/browser acceptance and no runtime input mutation."}))


if __name__ == "__main__":
    main()
