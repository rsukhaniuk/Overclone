#!/usr/bin/env python3
"""Build an additive font-only candidate from verified outlines/genuine MF output.

Does not edit accepted runtime pins, formats, workers or application defaults.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re


def pin(b):
    return {"length": len(b), "sha256": hashlib.sha256(b).hexdigest()}


def main():
    p = argparse.ArgumentParser(description=__doc__)
    for n in ("runtime", "inventory", "acquired", "generated", "output"):
        p.add_argument("--" + n, type=Path, required=True)
    a = p.parse_args()
    if a.output.exists():
        p.error("fresh overlay directory required")
    a.output.mkdir(parents=True)
    dest = a.output / "files"
    dest.mkdir()
    base = {r["name"]: r for r in json.loads((a.runtime / "pins.json").read_text())}
    inventory = json.loads(a.inventory.read_text())
    acquisition = json.loads((a.acquired / "acquisition.json").read_text())
    downloaded = {r["name"]: r for r in acquisition["files"] if "error" not in r}
    generation = json.loads((a.generated / "generation.json").read_text())
    metrics = {r["name"]: r for r in generation["rows"]}
    assert generation["generatedCount"] == generation["requiredCount"] == 241
    rows, controls = [], []

    def add(name, data, origin):
        identity = pin(data)
        if name in base:
            assert identity == {k: base[name][k] for k in ("length", "sha256")}, "existing resource drift: " + name
            controls.append({"name": name, **identity, "unchanged": True})
            return
        assert not (dest / name).exists()
        (dest / name).write_bytes(data)
        rows.append({"name": name, **identity, "origin": origin})

    for name in sorted({r["metric"] for r in inventory["faces"]}):
        row = metrics[name]
        data = (a.generated / name[:-4] / name).read_bytes()
        assert pin(data) == {k: row[k] for k in ("length", "sha256")}
        add(name, data, {"type": "genuine-native-metafont", "nativeMetafont": generation["nativeMetafont"],
                         "base": generation["base"], "generationReceiptSha256": pin((a.generated / "generation.json").read_bytes())["sha256"]})
    for name in sorted({r["outline"] for r in inventory["faces"]}):
        if name in base:
            data = (a.runtime / "files" / name).read_bytes()
            add(name, data, {"type": "unchanged-base"})
            continue
        row = downloaded[name]
        data = (a.acquired / "downloads" / name).read_bytes()
        assert pin(data) == {k: row[k] for k in ("length", "sha256")}
        cursor, segments = 0, []
        while cursor < len(data):
            assert data[cursor] == 128 and cursor + 2 <= len(data), name
            kind = data[cursor + 1]
            cursor += 2
            if kind == 3:
                assert cursor == len(data)
                break
            assert kind in (1, 2) and cursor + 4 <= len(data), name
            size = int.from_bytes(data[cursor:cursor + 4], "little")
            cursor += 4
            assert cursor + size <= len(data)
            segments.append((kind, data[cursor:cursor + size]))
            cursor += size
        assert [kind for kind, _ in segments] == [1, 2, 1], name
        assert re.search(rb"/FontName\s+/[A-Za-z0-9-]+\s+def", segments[0][1]), name
        add(name, data, {"type": "original-cm-super-pfb", "url": row["url"], "resolvedUrl": row["resolvedUrl"],
                         "acquisitionReceiptSha256": pin((a.acquired / "acquisition.json").read_bytes())["sha256"]})
    assert len(rows) == 476 and len(controls) == 4
    summary = {"schemaVersion": 1, "kind": "additive-font-candidate-not-adopted", "productRuntimeChanged": False,
               "basePins": pin((a.runtime / "pins.json").read_bytes()), "inventory": pin(a.inventory.read_bytes()),
               "files": rows, "unchangedControls": controls, "totalAddedBytes": sum(x["length"] for x in rows),
               "sourceDeclarationsComplete": True, "unicodeFidelityFixed": False,
               "scope": "Resource existence/provenance only. Native font rendering, memory and app/browser acceptance remain separate."}
    (a.output / "manifest.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps({"additions": len(rows), "totalAddedBytes": summary["totalAddedBytes"], "unchangedControls": len(controls)}))


if __name__ == "__main__":
    main()
