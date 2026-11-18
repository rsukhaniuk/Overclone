"""Offline pinned SDK build, exact-parent gate, independent output receipts."""
from pathlib import Path
import argparse
import json
import subprocess
from prepare import pin, prepare

SDK = "emscripten/emsdk:6.0.13@sha256:fb7864e398acfda863dfcea78b8bc8adad359fa60c6a0bbeb845931de64aa68a"
PARENT = {
    "e1.js": {"bytes": 14103, "sha256": "968aae4935523f528e2314c4dbd831b2dd326ac8d3de3444fd033b73463b0225"},
    "e1.wasm": {"bytes": 3716447, "sha256": "8b715ed561ec644757f305ddad0a99ae728c6705f9868e96ccb53c8c9143c4b3"},
}


def build(source, destination):
    source = source.resolve()
    destination = destination.resolve()
    prepare(source, destination)
    commands = []
    for kind in ("parent", "candidate"):
        output = destination / kind
        output.mkdir()
        command = [
            "docker", "run", "--rm", "--pull=never", "--network", "none", "--cpus=2", "--memory=3g",
            "-v", f"{source}:/pdfium-source:ro",
            "-v", f"{destination / (kind + '-wrapper')}:/wrapper:ro",
            "-v", f"{output}:/outputs",
            "-e", "E1_PDFIUM_SOURCE=/pdfium-source",
            "-e", "E1_PDFIUM_GENERATED=/pdfium-source/out/r2/gen",
            SDK, "sh", "/wrapper/build.sh", "/pdfium-source/public",
            "/pdfium-source/out/r2/obj/libpdfium.a", "/outputs",
        ]
        commands.append(command)
        with (destination / f"{kind}.log").open("wb") as log:
            subprocess.run(command, check=True, stdout=log, stderr=subprocess.STDOUT)
        actual = {name: pin(output / name) for name in PARENT}
        if kind == "parent" and actual != PARENT:
            raise SystemExit("frozen parent byte reproduction failed; candidate was not built")
        (destination / f"{kind}.json").write_text(json.dumps(actual, indent=2) + "\n")
    receipt = {"schemaVersion": 1, "sdk": SDK, "commands": commands, "inputReceipt": pin(destination / "inputs.json"),
               "parent": PARENT, "candidate": {name: pin(destination / "candidate" / name) for name in PARENT}}
    (destination / "build.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print(json.dumps(receipt["candidate"]))


if __name__ == "__main__":
    args = argparse.ArgumentParser()
    args.add_argument("--pdfium-source", type=Path, required=True)
    args.add_argument("--destination", type=Path, required=True)
    opts = args.parse_args()
    build(opts.pdfium_source, opts.destination)
