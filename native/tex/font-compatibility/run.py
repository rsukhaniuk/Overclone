#!/usr/bin/env python3
"""Repeat the bounded native ActualText gate using cached Docker/WASM inputs.

Exit 0 means all candidate gates pass; exit 2 means an executed candidate gate
failed. Neither result adopts a format, changes project source, or proves browser
acceptance. No downloads or tool installations occur.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]


def digest(data):
    return hashlib.sha256(data).hexdigest()


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--runtime", type=Path, required=True, help="complete cached compiler directory with pins.json/files")
    p.add_argument("--renderer", type=Path, required=True, help="accepted E1 directory containing e1.js/e1.wasm")
    p.add_argument("--results", type=Path, required=True, help="fresh directory outside source/input trees")
    p.add_argument("--image", default="oop-dev-tex-bootstrap:20261112")
    p.add_argument("--preflight", action="store_true")
    a = p.parse_args()
    runtime, renderer, out = [q.resolve() for q in (a.runtime, a.renderer, a.results)]
    for q in (REPO, runtime, renderer):
        if out == q or q in out.parents or out in q.parents:
            p.error("results must not overlap source/input trees")
    for name in ("docker", "pdftotext", "pdftoppm"):
        if not shutil.which(name):
            p.error("existing tool required (never auto-installed): " + name)
    for q in (runtime / "pins.json", renderer / "e1.js", renderer / "e1.wasm"):
        if not q.is_file():
            p.error("missing explicit cached input: " + str(q))
    subprocess.run(["docker", "image", "inspect", a.image], check=True, stdout=subprocess.DEVNULL)
    if a.preflight:
        print(json.dumps({"preflight": "pass", "executedNative": False, "candidateAccepted": False}))
        return 0
    if out.exists():
        p.error("fresh results directory required")
    out.mkdir(parents=True)
    base = ["docker", "run", "--rm", "--pull", "never", "--network", "none", "--read-only",
            "--cpus", "1", "--memory", "1g", "--pids-limit", "64", "--user", f"{os.getuid()}:{os.getgid()}",
            "--tmpfs", "/tmp:rw,nosuid,nodev,size=64m", "-v", f"{REPO}:/repo:ro",
            "-v", f"{runtime}:/runtime:ro", "-v", f"{renderer}:/renderer:ro", "-v", f"{out}:/out", a.image, "node"]
    calls = []

    def native(tool, args, label):
        command = base + ["/repo/native/tex/font-compatibility/" + tool, *args]
        with (out / (label + ".driver.log")).open("w") as log:
            result = subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, timeout=60)
        calls.append({"tool": tool, "arguments": args, "exitCode": result.returncode})
        assert result.returncode == 0, label + " native execution failed; inspect driver log"
        print(label + " executed", flush=True)

    for engine in ("pdftex", "latex"):
        for variant in ("control", "candidate"):
            native("probe.cjs", ["/runtime", engine, "format-control" if variant == "control" else "format",
                                 "/repo/native/tex/latex-dvi/latex-dvi.ini", "-", f"/out/{engine}-{variant}-format"],
                   engine + "-" + variant + "-format")
    fixtures = {"original": "docs/examples/t2a-project-font-main.tex",
                "mixed": "tests/PackageFontFixtures/fonts/mixed-semantic.tex",
                "ligature": "tests/PackageFontFixtures/fonts/mixed-ligature.tex"}
    observations = []
    for name, fixture in fixtures.items():
        for engine in (("pdftex",) if name == "ligature" else ("pdftex", "latex")):
            variants = {}
            for variant in ("control", "candidate"):
                label = f"{name}-{engine}-{variant}"
                native("probe.cjs", ["/runtime", engine, "candidate", "/repo/" + fixture,
                                     f"/out/{engine}-{variant}-format/pdflatex.fmt", "/out/" + label], label)
                pdf = "/out/" + label + "/main.pdf"
                geometry = None
                if engine == "latex":
                    pdf = "/out/" + label + ".pdf"
                    native("convert.cjs", ["/runtime", "/out/" + label + "/main.dvi", pdf], label + "-convert")
                    geometry = json.loads((out / (label + ".pdf.json")).read_text())["geometry"]
                native("renderer.cjs", ["/renderer", pdf, "/out/" + label + ".e1.json"], label + "-e1")
                local_pdf = out / pdf.removeprefix("/out/")
                poppler = subprocess.run(["pdftotext", "-enc", "UTF-8", str(local_pdf), "-"], capture_output=True, check=True).stdout
                raster = subprocess.run(["pdftoppm", "-r", "144", "-singlefile", "-png", str(local_pdf)], capture_output=True, check=True).stdout
                (out / (label + ".poppler.txt")).write_bytes(poppler)
                (out / (label + ".png")).write_bytes(raster)
                native_receipt = json.loads((out / label / "receipt.json").read_text())
                assert native_receipt["sourceUnchanged"]
                assert native_receipt["fixture"]["sha256"] == digest((REPO / fixture).read_bytes())
                variants[variant] = {"pdfSha256": digest(local_pdf.read_bytes()), "poppler": poppler.decode(),
                                     "popplerRasterSha256": digest(raster), "geometry": geometry,
                                     "e1": json.loads((out / (label + ".e1.json")).read_text())}
            control, candidate = variants["control"], variants["candidate"]
            e1_text = "\n".join(x["text"] for x in candidate["e1"]["pages"])
            gates = {"sourceUnchanged": True, "pageCountPreserved": control["e1"]["pageCount"] == candidate["e1"]["pageCount"],
                     "independentRasterPreserved": control["popplerRasterSha256"] == candidate["popplerRasterSha256"],
                     "currentRendererRasterPreserved": [x["raster"] for x in control["e1"]["pages"]] == [x["raster"] for x in candidate["e1"]["pages"]],
                     "cyrillicIndependentExtraction": "Іі" in candidate["poppler"], "cyrillicCurrentRendererExtraction": "Іі" in e1_text}
            if name == "mixed":
                gates["mixedLatinIndependentExtraction"] = "ASCII I i" in candidate["poppler"]
                gates["mixedLatinCurrentRendererExtraction"] = "ASCII I i" in e1_text
                for query in ("Привіт", "Іі", "ASCII I i"):
                    gates["currentRendererSearch:" + query] = bool(candidate["e1"]["pages"][0]["search"][query])
            if engine == "latex":
                gates["dviNavigationGeometryPreserved"] = bool(control["geometry"] and candidate["geometry"] and
                    all(x["status"] == "available" for x in control["geometry"]["pages"] + candidate["geometry"]["pages"]))
            observations.append({"fixture": name, "sourceSha256": digest((REPO / fixture).read_bytes()), "engine": engine,
                                 "gates": gates, "control": control, "candidate": candidate})
    accepted = all(all(x["gates"].values()) for x in observations)
    report = {"schemaVersion": 1, "executionStatus": "complete", "candidateAccepted": accepted,
              "productRuntimeChanged": False, "browserAcceptanceExecuted": False,
              "markerSha256": digest((HERE / "actualtext.tex").read_bytes()), "nativeCalls": calls, "observations": observations,
              "independentTools": {n: subprocess.run([n, "-v"], capture_output=True).stderr.decode().splitlines()[0] for n in ("pdftotext", "pdftoppm")}}
    (out / "receipt.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps({"executionStatus": "complete", "candidateAccepted": accepted,
                      "failedGates": [{"fixture": x["fixture"], "engine": x["engine"], "gates": [k for k, v in x["gates"].items() if not v]} for x in observations if not all(x["gates"].values())]}, ensure_ascii=False))
    return 0 if accepted else 2


if __name__ == "__main__":
    raise SystemExit(main())
