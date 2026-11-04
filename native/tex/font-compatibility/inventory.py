#!/usr/bin/env python3
"""Derive the T2A cmr/cmss/cmtt closure from retained FD/map/archive/index bytes.

No downloads, generated font aliases, resource staging or license clearance.
Cached index sizes are claims from those bytes, never acquired-file hashes.
"""
import argparse
from decimal import Decimal
import hashlib
import json
from pathlib import Path
import re
import tarfile


def pin(data):
    return {"length": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def inventory(runtime, metric_archive, outline_index):
    pins = json.loads((runtime / "pins.json").read_text())
    known = {p["name"]: p for p in pins}

    def resource(name):
        data = (runtime / "files" / name).read_bytes()
        assert pin(data) == {k: known[name][k] for k in ("length", "sha256")}, name
        return data

    map_bytes = resource("pdftex.map")
    mapping = {}
    for line in map_bytes.decode().splitlines():
        if not line or line.startswith("%"):
            continue
        name = line.split()[0]
        outlines = re.findall(r"<([^\s<>]+\.pfb)", line)
        encodings = re.findall(r"<([^\s<>]+\.enc)", line)
        if outlines:
            assert name not in mapping, name
            mapping[name] = {"outline": outlines[0], "encodings": encodings}
    archive_bytes = metric_archive.read_bytes()
    metrics = {}
    with tarfile.open(metric_archive, "r:xz") as archive:
        for member in archive.getmembers():
            if member.isfile() and "/lh-t2a/" in member.name and member.name.endswith(".tfm"):
                data = archive.extractfile(member).read()
                metrics[Path(member.name).name] = {"member": member.name, **pin(data)}
    index_bytes = outline_index.read_bytes()
    sizes = {name: int(size) for name, size in re.findall(
        r'<a href="([^"/]+\.pfb)">[^<]+</a>[^\n]*?\s(\d+)\s*$',
        index_bytes.decode(), re.MULTILINE)}
    sizes_declared = ["5", "6", "7", "8", "9", "10", "10.95", "12", "14.4",
                      "17.28", "20.74", "24.88", "29.86", "35.83"]
    declarations, faces, substitutions = [], [], []
    for family in ("cmr", "cmss", "cmtt"):
        name = "t2a" + family + ".fd"
        raw = resource(name)
        # Remove comments before parsing the actual constrained upstream syntax.
        text = "\n".join(line.split("%", 1)[0] for line in raw.decode().splitlines())
        declared = re.search(r"\\providecommand\{\\EC@(?:tt)?family\}\[5\]\{(.*?)genb\*#5", text, re.S)
        assert declared and re.findall(r"<(\d+(?:\.\d+)?)>", declared[1]) == sizes_declared, name
        declarations.append({"name": name, **pin(raw)})
        found = re.findall(r"\\EC@(tt)?family\{T2A\}\{([^}]+)\}\{([^}]+)\}\{([^}]+)\}\{([^}]+)\}", text)
        assert len(found) == {"cmr": 10, "cmss": 6, "cmtt": 4}[family], name
        for tt, fam, series, shape, prefix in found:
            assert fam == family
            for size in sizes_declared:
                design = "0800" if tt and Decimal(size) <= 8 else f"{int(Decimal(size) * 100):04d}"
                faces.append({"family": family, "series": series, "shape": shape,
                              "requestedPt": size, "metric": prefix + design + ".tfm",
                              "selection": "shared-8pt-design" if tt and Decimal(size) < 8 else "direct-design"})
        aliases = re.findall(r"\\DeclareFontShape\{T2A\}\{([^}]+)\}\{([^}]+)\}\{([^}]+)\}\s*\{<->(s?sub)\*([^}]+)\}", text)
        direct = len(re.findall(r"\\DeclareFontShape\{T2A\}\{" + family + r"\}", text))
        assert direct == len(aliases) + (1 if family == "cmss" else 0), "unparsed font declaration: " + name
        substitutions += [{"family": f, "series": s, "shape": sh, "kind": kind, "target": target,
                           "advertisedReady": False} for f, s, sh, kind, target in aliases]
        if family == "cmss":
            special = re.search(r"\\DeclareFontShape\{T2A\}\{cmss\}\{sbc\}\{n\}\{(.*?)lassdc10", text, re.S)
            assert special
            for size in re.findall(r"<(\d+(?:\.\d+)?)>", special[1]):
                faces.append({"family": family, "series": "sbc", "shape": "n", "requestedPt": size,
                              "metric": "lassdc10.tfm", "selection": "fixed-10pt-design"})
    for row in faces:
        metric = row["metric"]
        mapped = mapping.get(metric[:-4])
        assert mapped, "missing map association: " + metric
        row.update(mapped)
        row["runtimeMetric"] = {k: known[metric][k] for k in ("length", "sha256")} if metric in known else None
        row["cachedMetric"] = metrics.get(metric)
        outline = row["outline"]
        row["runtimeOutline"] = {k: known[outline][k] for k in ("length", "sha256")} if outline in known else None
        row["outlineIndexLength"] = sizes.get(outline)
        row["runtimeComplete"] = bool(row["runtimeMetric"] and row["runtimeOutline"] and all(e in known for e in row["encodings"]))
    missing_outlines = sorted({r["outline"] for r in faces if not r["runtimeOutline"]})
    missing_metrics = sorted({r["metric"] for r in faces if not r["runtimeMetric"] and not r["cachedMetric"]})
    assert all(name in sizes for name in missing_outlines), "outline missing from retained index"
    return {"schemaVersion": 1, "status": "incomplete-acquisition-required", "completeFontClosure": False,
            "scope": "Original T2A cmr/cmss/cmtt declarations only; not every installed T2A family or font availability proof",
            "inputs": {"runtimePins": pin((runtime / "pins.json").read_bytes()), "declarations": declarations,
                       "map": pin(map_bytes), "metricArchive": pin(archive_bytes), "outlineIndex": pin(index_bytes)},
            "faces": faces, "substitutions": substitutions,
            "totals": {"declaredSelections": len(faces), "runtimeCompleteSelections": sum(r["runtimeComplete"] for r in faces),
                       "uniqueMetrics": len({r["metric"] for r in faces}), "cachedArchiveT2AMetrics": len(metrics),
                       "missingMetricFilesBeyondCache": len(missing_metrics), "missingOutlineFiles": len(missing_outlines),
                       "missingOutlineBytesFromRetainedIndex": sum(sizes[n] for n in missing_outlines)},
            "acquisition": {"authorized": False, "outlineBaseUrl": "https://mirrors.ctan.org/fonts/ps-type1/cm-super/pfb/",
                            "outlines": [{"name": n, "indexLength": sizes[n]} for n in missing_outlines],
                            "metricPackagePage": "https://ctan.org/pkg/lh", "metricPackageUrl": None,
                            "missingMetricsBeyondCache": missing_metrics, "metricDownloadBytes": None,
                            "noticeEvidence": ["CM-Super retained README/COPYING/INSTALL", "LH full-package font notices require association", "LaTeX Cyrillic FD/source bundle LPPL notices"],
                            "caveat": "Index lengths are retained upstream claims, not acquired hashes. Exact full-LH artifact URL/version/length still needs metadata resolution; no archive URL is guessed. New sources and distribution route need separate approval."}}


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--runtime", type=Path, required=True)
    p.add_argument("--metric-archive", type=Path, required=True)
    p.add_argument("--outline-index", type=Path, required=True)
    p.add_argument("--output", type=Path, required=True)
    a = p.parse_args()
    if a.output.exists():
        p.error("fresh output file required")
    result = inventory(a.runtime, a.metric_archive, a.outline_index)
    a.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps(result["totals"]))


if __name__ == "__main__":
    main()
