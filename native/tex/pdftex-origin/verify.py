#!/usr/bin/env python3
"""Exercise the real pinned parent and derivative with exact output comparisons."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess

HERE = Path(__file__).resolve().parent
REPO = HERE.parents[2]
RUNNER = REPO / "native/tex/page-origin-navigation/run-current-project.cjs"
CLOCK = REPO / "native/tex/synctex-fixtures/dvi/fixed-clock.cjs"
ROOT = Path("тези/my thesis.tex")


def pin(path):
    return {"length": path.stat().st_size, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}


class Verification:
    def __init__(self, runtime, candidate, renderer, out):
        if out.exists():
            raise SystemExit("Fresh verification directory required")
        self.out, self.runtime, self.candidate, self.renderer = out, runtime, candidate, renderer
        out.mkdir()
        self.fixture = HERE / "fixture"
        self.results = {}

    def capture(self, name, *, vendor=False, project=None, corpus=None, no_map=False):
        output = self.out / name
        extra = {}
        if not vendor:
            extra["PDF_CANDIDATE"] = self.candidate
        if project:
            extra["PROJECT"] = project
        if corpus:
            extra["CORPUS_CASE"] = corpus
        if no_map:
            extra["PDF_DIRECT_NO_MAP"] = "1"
        env = dict(os.environ, RUNTIME=self.runtime, **extra)
        with (self.out / (name + ".log")).open("w") as log:
            subprocess.run(["node", "-r", CLOCK, RUNNER, "pdf", "latex", output],
                           env=env, cwd=REPO, check=True, stdout=log, stderr=subprocess.STDOUT, timeout=20)
        return output

    def compare(self, name, *, project=None, corpus=None, expected, compare_parent=True, no_map=False):
        output = self.capture(name, project=project, corpus=corpus, no_map=no_map)
        attestation = json.loads((output / "origin-attestation.json").read_text())
        geometry_bytes = (output / "geometry.json").read_bytes()
        statuses = [item["status"] for item in json.loads(geometry_bytes)["pages"]] if geometry_bytes else []
        if expected in ("available", "unsupported"):
            assert statuses and all(status == expected for status in statuses), name
            assert attestation["rawLength"] > 0 and attestation["legacyZeroOrigin"] == 0, name
        elif expected == "legacy":
            assert not statuses and attestation["legacyZeroOrigin"] == 1 and attestation["rawLength"] > 0, name
        elif expected == "no-map":
            assert not statuses and attestation["legacyZeroOrigin"] == 0 and attestation["rawLength"] == 0, name
        elif expected == "page-budget":
            assert not statuses and attestation["legacyZeroOrigin"] == 0 and attestation["rawLength"] > 0, name
        else:
            raise ValueError(expected)

        exact = {}
        if compare_parent:
            parent = self.capture(name + "-parent", vendor=True, project=project, corpus=corpus)
            for old in parent.iterdir():
                if old.name in ("receipt.json", "geometry.json"):
                    continue
                new = output / old.name
                a, b = pin(old), pin(new)
                assert a == b, old.name
                exact[old.name] = a
        self.results[name] = {
            "expected": expected, "geometry": pin(output / "geometry.json"),
            "attestation": attestation, "statuses": statuses, "exactParentOutputs": exact,
        }
        return output

    def variant(self, name, change):
        target = self.out / (name + "-project")
        shutil.copytree(self.fixture, target)
        source = target / ROOT
        source.write_text(change(source.read_text()))
        return target

    def run(self):
        measured = self.compare("four-rotations", project=self.fixture, expected="available")
        with (self.out / "oracle.log").open("w") as log:
            oracle = self.out / "oracle"
            subprocess.run(["node", HERE / "oracle.cjs", self.fixture, self.renderer,
                            self.runtime / "files", measured, oracle], cwd=REPO, check=True,
                           stdout=log, stderr=subprocess.STDOUT, timeout=20)
        self.compare("no-map", project=self.fixture, no_map=True, compare_parent=False, expected="no-map")
        for name in ("portable", "packages", "biblatex"):
            self.compare(name, corpus=name, expected="legacy")

        before = "\\pdfpageattr{/MediaBox [12 18 612 818] /CropBox [17 23 607 813] /Rotate 0}"
        material = {
            "duplicate-media": before.replace("/Rotate", "/MediaBox [0 0 600 800] /Rotate"),
            "duplicate-crop": before.replace("/Rotate", "/CropBox [10 15 605 815] /Rotate"),
            "wrong-case-rotate": before.replace("/Rotate", "/rotate"),
            "invalid-rotate": before.replace("/Rotate 0", "/Rotate 45"),
            "rotate-name-boundary": before.replace("/Rotate 0", "/Rotate0"),
            "rotate-noninteger": before.replace("/Rotate 0", "/Rotate 0.0"),
            "user-unit": before.replace("/Rotate 0", "/Rotate 0 /UserUnit 2"),
            "comment-media": "\\pdfpageattr{/Fake (/MediaBox [12 18 612 818])}",
            "indirect": before.replace("[12 18 612 818]", "2 0 R"),
            "crop-outside": before.replace("[17 23 607 813]", "[0 0 613 820]"),
            "dimension-range": before.replace("612 818", "1000001 818"),
        }
        for name, after in material.items():
            self.compare(name, project=self.variant(name, lambda source: source.replace(before, after, 1)),
                         expected="unsupported")

        def late(source, inserted):
            return source.replace("\\end{document}", inserted + "\n\\end{document}", 1)

        self.compare("late-root-attributes", project=self.variant("late-root-attributes", lambda source:
            late(source, "\\pdfpagesattr{/Rotate 90}")), expected="unsupported")
        self.compare("reset-root-attributes", project=self.variant("reset-root-attributes", lambda source:
            late(source, "\\pdfpagesattr{/Rotate 90}\\pdfpagesattr{}")), expected="available")
        self.compare("opaque-explicit-ctm", project=self.variant("opaque-explicit-ctm", lambda source:
            source.replace("\\kern38bp", "\\pdfliteral page {1 0 0 1 2 0 cm}\n\\kern38bp", 1)),
            expected="unsupported")
        self.compare("printed-colors", project=self.variant("printed-colors", lambda source:
            source.replace("\\kern38bp", "\\pdfliteral page {0.2 0.4 0.6 rg .5 G}\n\\kern38bp", 1)),
            expected="available")
        self.compare("malformed-color", project=self.variant("malformed-color", lambda source:
            source.replace("\\kern38bp", "\\pdfliteral page {0 +g}\n\\kern38bp", 1)),
            expected="unsupported")
        self.compare("catalog-override", project=self.variant("catalog-override", lambda source:
            late(source, "\\pdfcatalog{/Pages 1 0 R}")), expected="unsupported")
        self.compare("direct-box-delimiter", project=self.variant("direct-box-delimiter", lambda source:
            source.replace("/MediaBox [", "/MediaBox[").replace("/CropBox [", "/CropBox[")),
            expected="available")
        self.compare("mixed-built-in", project=self.variant("mixed-built-in", lambda source:
            source.replace(before, "\\pdfpageattr{/Rotate 0}", 1)), expected="available")
        self.compare("ordinary-measured", project=self.variant("ordinary-measured", lambda source:
            re.sub(r"\\\\pdfpageattr\{[^}]*\}\n", "", source)), expected="available")

        mag = self.compare("magnified", project=self.variant("magnified", lambda source:
            "\\mag=1200\n" + source), expected="available")
        assert "Magnification:1200\n" in (mag / "raw.synctex").read_text()
        magnified = json.loads((mag / "geometry.json").read_text())
        assert all(abs(page["writerToPdfRaw"][5] - 960) < .01 for page in magnified["pages"])

        many = self.variant("page-budget", lambda source:
            """\\documentclass{article}
\\begin{document}
\\newcount\\Sheet
\\loop\\shipout\\hbox{Page with anchor words}\\advance\\Sheet1
\\ifnum\\Sheet<513\\repeat
\\end{document}
""")
        self.compare("page-budget", project=many, expected="page-budget")

        evidence = {"status": "PASS", "cases": self.results, "oracle": pin(oracle / "receipt.json")}
        (self.out / "verification.json").write_text(json.dumps(evidence, indent=2) + "\n")
        print(f"{len(self.results)} real paired/guard tests; 12 exact forward/inverse glyph links on four rotations")


if __name__ == "__main__":
    cli = argparse.ArgumentParser(description=__doc__)
    for field in ("runtime", "candidate", "renderer", "out"):
        cli.add_argument("--" + field, required=True, type=lambda path: Path(path).resolve())
    args = cli.parse_args()
    Verification(args.runtime, args.candidate, args.renderer, args.out).run()
