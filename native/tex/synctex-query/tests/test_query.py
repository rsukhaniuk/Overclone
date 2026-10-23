#!/usr/bin/env python3
"""Offline native-only tests against REAL upstream parser and independent CLI.

Usage: test_query.py DRIVER PINNED_SOURCE UPSTREAM_CLI PRIVATE_OUTPUT_DIRECTORY
The caller builds DRIVER; every process gets an exclusively owned temporary CWD.
Synthetic maps below are labelled; none claims producer/PDF correlation.
"""
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import tempfile

DRIVER, SOURCE, CLI, OUTPUT = map(lambda s: Path(s).resolve(), sys.argv[1:])
OUTPUT.mkdir(parents=True, exist_ok=True)
FIXTURE = SOURCE / "texk/web2c/synctexdir/tests/footest.synctex"
checks = 0


def run_map(data, operation, *args, setup=None):
    global checks
    with tempfile.TemporaryDirectory(prefix="sq-test-", dir=OUTPUT) as tmp:
        cwd = Path(tmp)
        map_file = cwd / "input.synctex"
        map_file.write_bytes(data)
        if setup:
            setup(cwd)
        command = [str(DRIVER), str(map_file), operation, *map(str, args)]
        result = subprocess.run(command, cwd=cwd, capture_output=True, timeout=15)
        assert result.returncode == 0, (command, result.returncode, result.stdout, result.stderr)
        text = result.stdout.decode("utf-8", errors="strict")
        records = [line for line in text.splitlines() if line.startswith("SQ_RESULT ")]
        assert len(records) == 1, text
        _, status, payload = records[0].split(" ", 2)
        if not setup:
            assert not (cwd / ".synctex-query-private").exists(), "sidecar not cleaned"
        checks += 1
        return int(status), json.loads(payload)


def synthetic(name=b"main.tex", pages=1, width=655360, page_start=1):
    """Small canonical v1 fixture, authored for tests, parsed by upstream code."""
    data = (b"SyncTeX Version:1\nInput:1:" + name + b"\nOutput:pdf\nMagnification:1000\n"
            b"Unit:1\nX Offset:0\nY Offset:0\nContent:\n")
    for page in range(page_start, page_start + pages):
        data += (f"{{{page}\n[1,10:655360,1310720:{width},655360,0\n"
                 f"(1,10:655360,1310720:{width},655360,0\n"
                 "x1,10:700000,1310720\n)\n]\n" + f"}}{page}\n").encode()
    return data + b"Postamble:\nCount:3\nPost scriptum:\n"


def cli(map_bytes, operation, *args):
    # The oracle is a separate upstream executable, not a callback or fake parser.
    with tempfile.TemporaryDirectory(prefix="sq-oracle-", dir=OUTPUT) as tmp:
        root = Path(tmp)
        (root / "oracle.synctex").write_bytes(map_bytes)
        pdf = root / "oracle.pdf"
        # CLI requires the output path; this is NOT a generated PDF or evidence
        # of map/PDF correlation. Neither upstream query reads PDF contents.
        pdf.write_bytes(b"")
        if operation == "edit":
            command = [str(CLI), operation, "-o", f"{args[1]}:{pdf}"]
        else:
            command = [str(CLI), operation, *map(str, args), "-o", str(pdf)]
        environment = {k: v for k, v in os.environ.items()
                       if k not in ("SYNCTEX_EDITOR", "SYNCTEX_VIEWER")}
        done = subprocess.run(command, cwd=root, env=environment,
                              capture_output=True, timeout=15, check=True)
        text = done.stdout.decode("utf-8")
        (OUTPUT / f"cli-{operation}-{checks}.txt").write_text(text)
        assert "SyncTeX result begin" in text and "SyncTeX result end" in text, text
        matches = []
        current = None
        for line in text.splitlines():
            if line.startswith("Output:"):
                current = {}
                matches.append(current)
            elif current is not None and ":" in line:
                k, v = line.split(":", 1)
                current[k] = v
        return matches


original = FIXTURE.read_bytes()
status, forward = run_map(original, "forward", "test.tex", 100)
assert status == 0 and forward["schema"] == 1 and forward["direction"] == "forward"
assert forward["coordinateSpace"] == "synctex-visible-bp-top-left"
assert forward["retainedCount"] == 17 and not forward["truncated"]
oracle = cli(original, "view", "-i", "100:0:test.tex")
assert len(oracle) == 17
for match, expected in zip(forward["matches"], oracle):
    assert match["file"] == "test.tex" and match["tag"] == 1
    # Upstream weak query semantics return native line 99 for request 100.
    assert match["line"] == 99 and match["column"] is None
    assert match["page"] == int(expected["Page"])
    for actual, key in [(match["point"]["h"], "x"), (match["point"]["v"], "y"),
                        (match["box"]["h"], "h"), (match["box"]["width"], "W")]:
        assert math.isclose(actual, float(expected[key]), abs_tol=1e-5)
    # CLI reports bottom edge v and total H; ABI retains native baseline/depth.
    assert math.isclose(match["box"]["v"] + match["box"]["depth"], float(expected["v"]), abs_tol=2e-5)
    assert math.isclose(match["box"]["height"] + match["box"]["depth"], float(expected["H"]), abs_tol=2e-5)
first = forward["matches"][0]
status, inverse = run_map(original, "inverse", first["page"], first["point"]["h"], first["point"]["v"])
assert status == 0 and inverse["direction"] == "inverse"
oracle = cli(original, "edit", "-i", f'{first["page"]}:{first["point"]["h"]}:{first["point"]["v"]}')
assert len(inverse["matches"]) == len(oracle)
for match, expected in zip(inverse["matches"], oracle):
    assert match["file"] == expected["Input"]
    assert match["line"] == int(expected["Line"])
    assert match["column"] is None and int(expected["Column"]) in (-1, 0)
assert run_map(original, "controls") == (0, None)
assert run_map(original, "close-first") == (0, None)

# Synthetic, not engine-produced: spaces, Ukrainian/emoji, JSON quote/backslash.
for name in ["chapter/my thesis.tex", "розділ/теза 😀.tex", 'a"quoted.tex']:
    data = synthetic(name.encode())
    status, result = run_map(data, "forward", name, 10)
    assert status == 0 and result["matches"][0]["file"] == name
    oracle = cli(data, "view", "-i", f"10:0:{name}")
    assert len(oracle) == result["retainedCount"]
    point = result["matches"][0]
    status, backward = run_map(data, "inverse", point["page"], point["point"]["h"], point["point"]["v"])
    assert status == 0 and backward["matches"][0]["file"] == name
# Native names are retained as DATA, even absolute/non-project paths/backslashes.
for name in [b"/not-a-project/main.tex", b"dir\\main.tex"]:
    status, result = run_map(synthetic(name), "inverse", 1, 10, 20)
    assert status == 0 and result["matches"][0]["file"] == name.decode()

for pages, truncated in [(32, False), (33, True), (34, True)]:
    data = synthetic(pages=pages)
    status, result = run_map(data, "forward", "main.tex", 10)
    assert status == 0 and result["retainedCount"] == 32 and result["truncated"] == truncated
    assert len(cli(data, "view", "-i", "10:0:main.tex")) == pages
# Boundary pages and coordinates are admitted, absent pages still mean EMPTY.
assert run_map(synthetic(page_start=512), "forward", "main.tex", 10)[0] == 0
assert run_map(synthetic(page_start=513), "forward", "main.tex", 10) == (-5, None)
assert run_map(original, "inverse", 512, -1000000, 1000000) == (1, None)

# Reject malformed/unsupported native DATA, without guessing a replacement.
for name in [b"bad\x80.tex", b"bad\x1b.tex", b"bad\xc0\xaf.tex"]:
    assert run_map(synthetic(name), "inverse", 1, 10, 20) == (-5, None)
assert run_map(synthetic(b"x" * 1025), "inverse", 1, 10, 20) == (-6, None)
# Per-string bounds alone cannot exceed the aggregate JSON budget.
assert run_map(synthetic(b"x" * 1000, pages=33), "inverse", 1, 10, 20)[0] == 0
assert run_map(synthetic(b"x" * 1000, pages=33), "forward", "x" * 1000, 10) == (-6, None)

for data, expected in [(b"", -1), (b"SyncTeX Version:1\n\0", -1),
                       (b"SyncTeX Version:2\n", -1), (b"\x1f\x8bgarbage", -1),
                       (b"x" * 1048576, -1), (b"SyncTeX Version:1\n", -4),
                       (b"SyncTeX Version:1\nInput:1:x\nContent:\n", -4)]:
    assert run_map(data, "load") == (expected, None)
# Exact upper byte boundary is admitted to upstream (trailing whitespace is not
# interpreted by the adapter); bytes beyond it are rejected before parsing.
data = synthetic()
assert run_map(data + b"\n" * (1048575 - len(data)), "load") == (0, None)

# Preexisting fixed entry is neither adopted nor removed (including symlinks).
for kind in ["file", "directory", "symlink"]:
    def setup(root, kind=kind):
        private = root / ".synctex-query-private"
        if kind == "file":
            private.write_bytes(b"sentinel")
        elif kind == "directory":
            private.mkdir()
            (private / "map.synctex").write_bytes(b"sentinel")
        else:
            (root / "sentinel").write_bytes(b"sentinel")
            private.symlink_to(root / "sentinel")
        # Keep evidence in this test process after child completion.
        setup.path = private
    # run_map preserves controls until this temporary directory is cleaned.
    with tempfile.TemporaryDirectory(prefix="sq-preexisting-", dir=OUTPUT) as tmp:
        root = Path(tmp)
        source = root / "input.synctex"
        source.write_bytes(original)
        setup(root)
        done = subprocess.run([str(DRIVER), str(source), "load"], cwd=root,
                              capture_output=True, timeout=15, check=True)
        assert b"SQ_RESULT -3 null" in done.stdout
        target = setup.path / "map.synctex" if kind == "directory" else setup.path
        assert target.read_bytes() == b"sentinel"
        if kind == "symlink":
            assert setup.path.is_symlink()
        checks += 1

print(f"PASS: {checks} isolated real-parser cases; CLI comparisons and in-process controls passed")
