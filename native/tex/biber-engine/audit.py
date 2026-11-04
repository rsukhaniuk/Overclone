#!/usr/bin/env python3
"""Inspect supplied local inputs without downloading or executing their code.

Exit 3: missing/incompatible/incompletely inspected inputs.
Exit 4: initial source candidates exist, but no browser build has been proved.
Neither result establishes executable availability or R1-D acceptance.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re


MARKERS = {
    "biberProgram": "bin/biber",
    "biberModule": "lib/Biber.pm",
    "biberVersion": "lib/Biber/Config.pm",
    "perlInterpreter": "perl.c",
    "perlHeader": "perl.h",
    "emscriptenPerlHints": "hints/emscripten.sh",
    "xmlLibXmlXs": "LibXML.xs",
    "xmlLibXsltXs": "LibXSLT.xs",
    "textBibtexXs": "BibTeX.xs",
}
MAX_ENTRIES = 100_000
MAX_MARKER_BYTES = 4 * 1024 * 1024


def pin(path):
    data = path.read_bytes()
    return {"length": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def inspect(biblatex, source_roots, perl_report=None):
    if biblatex.stat().st_size > MAX_MARKER_BYTES:
        raise ValueError("biblatex input exceeds inspection bound")
    text = biblatex.read_text(encoding="utf-8")
    versions = {}
    for key, macro in [("biblatex", "abx@version"), ("bcf", "blx@bcfversion"),
                       ("bbl", "blx@bblversion")]:
        values = re.findall(r"\\def\\" + re.escape(macro) + r"\{([^{}]+)\}", text)
        if len(values) != 1:
            raise ValueError("missing or ambiguous biblatex version macro: " + key)
        versions[key] = values[0]
    candidates = {key: [] for key in MARKERS}
    scans = []
    for label, root in source_roots:
        row = {"label": label, "entries": 0, "skippedSymlinks": 0, "errors": []}

        def onerror(error):
            row["errors"].append(type(error).__name__)

        for directory, dirs, files in os.walk(root, followlinks=False, onerror=onerror):
            dirs.sort()
            for name in dirs[:]:
                if (Path(directory) / name).is_symlink():
                    dirs.remove(name)
                    row["skippedSymlinks"] += 1
            for name in sorted(files):
                row["entries"] += 1
                if row["entries"] > MAX_ENTRIES:
                    raise ValueError("source root exceeds inspection entry bound: " + label)
                path = Path(directory) / name
                if path.is_symlink():
                    row["skippedSymlinks"] += 1
                    continue
                relative = path.relative_to(root).as_posix()
                for key, suffix in MARKERS.items():
                    if relative == suffix or relative.endswith("/" + suffix):
                        if path.stat().st_size > MAX_MARKER_BYTES:
                            raise ValueError("source marker exceeds inspection bound: " + key)
                        candidates[key].append({"root": label, "path": relative, **pin(path)})
        scans.append(row)
    blockers = []
    if versions != {"biblatex": "3.19", "bcf": "3.10", "bbl": "3.2"}:
        blockers.append("biblatex-version-pairing-requires-review")
    if any(row["errors"] or row["skippedSymlinks"] for row in scans):
        blockers.append("source-scan-incomplete")
    for key in MARKERS:
        if not candidates[key]:
            blockers.append("missing-" + key)
    report = {
        "schemaVersion": 1,
        "status": "blocked-inputs" if blockers else "source-candidates-build-unverified",
        "runtimeAvailable": False,
        "browserBcfToBblExecuted": False,
        "target": {"biber": "2.19", **versions},
        "biblatexPin": pin(biblatex),
        "sourceScans": scans,
        "sourceCandidates": candidates,
        "blockers": blockers,
        "limits": {"entriesPerRoot": MAX_ENTRIES, "markerBytes": MAX_MARKER_BYTES},
        "scope": "Marker presence is not provenance, a complete dependency closure, or a build.",
    }
    if perl_report is not None:
        if perl_report.stat().st_size > MAX_MARKER_BYTES:
            raise ValueError("native Perl probe exceeds inspection bound")
        native = json.loads(perl_report.read_text())
        if native.get("schemaVersion") != 1 or native.get("runtime") != "native-host-toolchain-only":
            raise ValueError("unexpected native Perl probe schema/runtime")
        report["nativeBuildTools"] = native
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--biblatex", type=Path, required=True)
    parser.add_argument("--source-root", action="append", required=True, metavar="LABEL=PATH")
    parser.add_argument("--perl-report", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    roots = []
    for item in args.source_root:
        label, separator, path = item.partition("=")
        root = Path(path)
        if not separator or not re.fullmatch(r"[a-zA-Z0-9_-]+", label) or not root.is_dir():
            parser.error("each source root must be an existing directory with LABEL=PATH")
        if label in [name for name, _ in roots]:
            parser.error("source root labels must be unique")
        roots.append((label, root.resolve()))
    if args.output.exists():
        parser.error("fresh output path required")
    try:
        report = inspect(args.biblatex, roots, args.perl_report)
        with args.output.open("x", encoding="utf-8") as output:
            json.dump(report, output, indent=2, ensure_ascii=False)
            output.write("\n")
    except (OSError, ValueError) as error:
        parser.error(str(error))
    print(report["status"] + "; browser Biber remains unavailable")
    return 3 if report["blockers"] else 4


if __name__ == "__main__":
    raise SystemExit(main())
