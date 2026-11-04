#!/usr/bin/env python3
"""Generate genuine LH TFM files with an explicitly built native Metafont.

Run inside the reserved network-disabled Docker job. Inputs are copied MF source
data and original LH source, never host executables or substituted metrics.
"""
import argparse
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess


def pin(path):
    b = path.read_bytes()
    return {"length": len(b), "sha256": hashlib.sha256(b).hexdigest()}


def main():
    p = argparse.ArgumentParser(description=__doc__)
    for n in ("mf", "mf-inputs", "lh", "inventory", "output"):
        p.add_argument("--" + n, type=Path, required=True)
    a = p.parse_args()
    if a.output.exists():
        p.error("fresh output required")
    a.output.mkdir(parents=True)
    inv = json.loads(a.inventory.read_text())
    names = sorted({r["metric"] for r in inv["faces"]})
    assert len(names) == 241 and all(re.fullmatch(r"[a-z0-9]+\.tfm", n) for n in names)
    env = dict(os.environ, MFINPUTS=str(a.mf_inputs) + "//:" + str(a.lh) + "//", MFBASES=str(a.output),
               MKTEXMF="0", SOURCE_DATE_EPOCH="1735689600", FORCE_SOURCE_DATE="1")
    # Raster generation is unnecessary: use original CM-Super outlines in the PDF.
    startup = r"\input plain; mode_def tfm_only = proofing:=-1; fontmaking:=1; pixels_per_inch:=600; blacker:=.35; fillin:=.1; o_correction:=.6; enddef; dump"
    with (a.output / "base-driver.log").open("w") as log:
        subprocess.run([str(a.mf), "-ini", "-interaction=nonstopmode", "-jobname=r1font", startup], cwd=a.output,
                       env=env, stdout=log, stderr=subprocess.STDOUT, timeout=30, check=True)

    def generate(name):
        job = name[:-4]
        out = a.output / job
        out.mkdir()
        # Original LH's fikparm derives family/shape/design size from jobname,
        # exactly as the original generated per-font 'input fikparm;' stubs do.
        command = [str(a.mf), "-base=r1font", "-recorder", "-interaction=nonstopmode", "-jobname=" + job,
                   r"\mode:=tfm_only; mag:=1; input fikparm"]
        with (out / "driver.log").open("w") as log:
            r = subprocess.run(command, cwd=out, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=30)
        metric = out / name
        if r.returncode or not metric.is_file():
            return {"name": name, "exitCode": r.returncode, "generated": False}
        b = metric.read_bytes()
        words = [int.from_bytes(b[i:i+2], "big") for i in range(0, 24, 2)]
        assert len(b) == words[0] * 4 and words[2] <= words[3] < 256 and words[1] >= 2, name
        inputs = []
        recorder = out / (job + ".fls")
        if recorder.is_file():
            for line in recorder.read_text().splitlines():
                if line.startswith("INPUT "):
                    file = Path(line[6:])
                    if not file.is_absolute():
                        file = out / file
                    if file.is_file():
                        inputs.append({"path": str(file), **pin(file)})
        assert inputs, "actual source-recorder witness required"
        return {"name": name, "exitCode": r.returncode, "generated": True, **pin(metric),
                "tfmHeader": words, "inputs": inputs, "command": command}

    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        rows = list(pool.map(generate, names))
    summary = {"schemaVersion": 1, "nativeMetafont": pin(a.mf), "base": pin(a.output / "r1font.base"),
               "inventory": pin(a.inventory), "generatedCount": sum(r["generated"] for r in rows),
               "requiredCount": len(names), "rows": rows, "productRuntimeChanged": False}
    (a.output / "generation.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps({k: summary[k] for k in ("generatedCount", "requiredCount")}))
    return 0 if summary["generatedCount"] == len(names) else 1


if __name__ == "__main__":
    raise SystemExit(main())
