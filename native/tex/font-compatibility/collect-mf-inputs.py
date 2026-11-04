#!/usr/bin/env python3
"""Copy explicitly supplied, unmodified plain/CM/EC MF source data with hashes.

Data inputs only; never invokes a host TeX/Metafont executable or downloads.
"""
import argparse
import hashlib
import json
from pathlib import Path


def main():
    p = argparse.ArgumentParser(description=__doc__)
    for n in ("plain", "cm", "ec", "output"):
        p.add_argument("--" + n, type=Path, required=True)
    a = p.parse_args()
    if a.output.exists():
        p.error("fresh output required")
    a.output.mkdir(parents=True)
    rows = []
    for kind, directory in (("plain", a.plain), ("cm", a.cm), ("ec", a.ec)):
        for source in sorted(directory.glob("*.mf")):
            data = source.read_bytes()
            if len(data) > 1048576:
                p.error("oversize MF input")
            target = a.output / source.name
            if target.exists():
                assert target.read_bytes() == data, "conflicting MF basename"
                continue
            target.write_bytes(data)
            rows.append({"component": kind, "source": str(source), "name": source.name,
                         "length": len(data), "sha256": hashlib.sha256(data).hexdigest()})
    assert {"plain.mf", "cmbase.mf", "ecrm.mf"} <= {r["name"] for r in rows}
    (a.output / "manifest.json").write_text(json.dumps({"schemaVersion": 1,
        "scope": "Explicit original MF source data; retain original per-file notices and source association", "files": rows}, indent=2) + "\n")
    print(json.dumps({"files": len(rows), "bytes": sum(r["length"] for r in rows)}))


if __name__ == "__main__":
    main()
