#!/usr/bin/env python3
"""Prepare an overlay only; this does NOT run INITEX or produce a format.

Use explicit --staged (catalog.json + files/), --legacy (pins.json + files/),
and --output (existing empty directory) roots. No symlinks are accepted in
roots, output, their existing ancestors, metadata or selected source paths.
Input/output ancestor overlap in either direction is rejected. JSON schemas
are bounded and strict; only selected file contents are read/verified.
All selected bytes, including the fixed engines, are validated and retained
before output creation. Emission uses exclusive creates. On write failure,
only entries created by this invocation and still bearing their original
inode identity are removed; the caller's output directory is never removed.
Inputs/output must not be concurrently modified. This is an offline packaging
helper, not a runtime isolation or complete distribution-licensing boundary.
"""
import argparse
import hashlib
import json
import os
import re
import stat
import sys
from contextlib import ExitStack
from pathlib import Path, PurePosixPath

ENGINES = {
    "engine.js": (72275, "39384532be2e32fd09dba99226db9da7572032c70da2bb2428fc9123c7fd163f"),
    "engine.wasm": (1779444, "7d239273b63b42d0c8f31d5e198a221c200c222f365760c20b9178408077048e"),
}
MAX_METADATA_BYTES = 1048576
MAX_FILES = 512
MAX_FILE_BYTES = 16777216
MAX_TOTAL_BYTES = 67108864
LEGACY_EXTENSIONS = frozenset({".tfm", ".pfb", ".enc", ".map"})
RESERVED = frozenset({*ENGINES, "pdflatex.fmt", "swiftlatexpdftex.fmt"})
AUTHORED = {
    "pdflatex.ini": b"% Private deterministic INITEX bootstrap, not a kernel patch.\n\\input pdftexconfig.tex\n\\year=2025 \\month=1 \\day=1 \\time=0\n\\scrollmode\n\\input latex.ltx\n\\endinput\n",
    "hyphen.cfg": b"% Private English-only language configuration.\n\\input hyphen.tex\n\\lefthyphenmin=2 \\righthyphenmin=3\n",
}
STAGED_DIRS = frozenset({
    "tex/latex/base", "tex/latex/l3kernel", "tex/latex/l3backend",
    "tex/latex/amsmath", "tex/latex/amsfonts", "tex/latex/graphics",
    "tex/latex/graphics-def", "tex/latex/graphics-cfg",
    "tex/generic/tex-ini-files", "tex/generic/iftex", "tex/generic/hyphen",
    "tex/generic/pdftex", "tex/generic/unicode-data",
    "fonts/tfm/public/amsfonts/cmextra",
})
DIR_FLAGS = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
FILE_FLAGS = os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK


def identity(data):
    return {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def require(ok, message):
    if not ok:
        raise ValueError(message)


def basename(value):
    require(isinstance(value, str) and re.fullmatch(r"[A-Za-z0-9_][A-Za-z0-9_.-]{0,199}", value) is not None,
            "invalid single basename")
    return value


def relative(value):
    require(isinstance(value, str) and 0 < len(value) <= 4096, "invalid relative source path")
    parts = value.split("/")
    for part in parts:
        basename(part)
    require(str(PurePosixPath(value)) == value, "noncanonical relative source path")
    return value


def sha(value):
    require(isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) is not None,
            "invalid SHA-256")


def size(value, maximum=MAX_FILE_BYTES):
    require(type(value) is int and 0 <= value <= maximum, "invalid declared size")


def shape(value, keys):
    require(isinstance(value, dict) and set(value) == set(keys), "invalid object schema")


def unique_object(pairs):
    obj = {}
    for key, value in pairs:
        require(key not in obj, "duplicate JSON object key")
        obj[key] = value
    return obj


def directory(value):
    """Reject symlinks in every existing component, including the leaf."""
    path = Path(os.path.abspath(value))
    for item in (*reversed(path.parents), path):
        mode = item.lstat().st_mode
        require(stat.S_ISDIR(mode) and not stat.S_ISLNK(mode), "root/output component is not a real directory")
    return path


def open_directory(stack, path, parent_fd=None):
    fd = os.open(path, DIR_FLAGS, dir_fd=parent_fd)
    stack.callback(os.close, fd)
    return fd


def read_bounded(root_fd, rel, expected_size=None, cap=MAX_FILE_BYTES):
    """Walk from an opened root/files descriptor; no component may be a symlink."""
    relative(rel)
    with ExitStack() as stack:
        fd = root_fd
        parts = rel.split("/")
        for part in parts[:-1]:
            fd = open_directory(stack, part, fd)
        leaf = os.open(parts[-1], FILE_FLAGS, dir_fd=fd)
        stack.callback(os.close, leaf)
        before = os.fstat(leaf)
        require(stat.S_ISREG(before.st_mode), "input is not a regular file")
        require(0 <= before.st_size <= cap, "actual file size exceeds budget")
        if expected_size is not None:
            require(before.st_size == expected_size, "declared/stat size mismatch")
        # The read itself is bounded even if the file grows after fstat.
        with os.fdopen(os.dup(leaf), "rb") as stream:
            data = stream.read(before.st_size + 1)
        after = os.fstat(leaf)
        require(len(data) == before.st_size and after.st_size == before.st_size
                and after.st_mtime_ns == before.st_mtime_ns, "input changed while reading")
        return data


def metadata(root_fd, name):
    raw = read_bounded(root_fd, name, cap=MAX_METADATA_BYTES)
    try:
        obj = json.loads(raw, object_pairs_hook=unique_object)
    except (ValueError, RecursionError) as error:
        raise ValueError("invalid bounded JSON metadata") from error
    return obj, raw


def schemas(catalog, pin_root):
    shape(catalog, ("kernelVersion", "patchLevel", "scope", "files"))
    require(isinstance(catalog["kernelVersion"], str) and len(catalog["kernelVersion"]) <= 64,
            "invalid kernel version")
    require(type(catalog["patchLevel"]) is int and 0 <= catalog["patchLevel"] <= 10000,
            "invalid patch level")
    require(isinstance(catalog["scope"], str) and len(catalog["scope"]) <= 4096, "invalid scope")
    shape(pin_root, ("resources",))
    rows, pins = catalog["files"], pin_root["resources"]
    for array in (rows, pins):
        require(isinstance(array, list) and 1 <= len(array) <= MAX_FILES, "invalid metadata entry count")
    paths, names = set(), set()
    total = 0
    for row in rows:
        shape(row, ("name", "relativePath", "sourcePath", "bytes", "sha256"))
        basename(row["name"])
        relative(row["relativePath"])
        require(PurePosixPath(row["relativePath"]).name == row["name"], "name/source basename mismatch")
        require(isinstance(row["sourcePath"], str) and len(row["sourcePath"]) <= 4096
                and "\0" not in row["sourcePath"], "invalid source descriptor")
        size(row["bytes"])
        sha(row["sha256"])
        key = row["relativePath"].casefold()
        require(key not in paths, "duplicate/case-colliding catalog source path")
        paths.add(key)
        total += row["bytes"]
    require(total <= MAX_TOTAL_BYTES, "catalog declared total exceeds budget")
    total = 0
    for row in pins:
        shape(row, ("name", "length", "sha256"))
        basename(row["name"])
        size(row["length"])
        sha(row["sha256"])
        key = row["name"].casefold()
        require(key not in names, "duplicate/case-colliding pin")
        names.add(key)
        total += row["length"]
    require(total <= MAX_TOTAL_BYTES, "pins declared total exceeds budget")
    return rows, pins


def prepare(staged_fd, legacy_fd):
    catalog, catalog_raw = metadata(staged_fd, "catalog.json")
    pin_root, pins_raw = metadata(legacy_fd, "pins.json")
    rows, pins = schemas(catalog, pin_root)
    plan, namespace = {}, {}

    def add(name, spec):
        key = name.casefold()
        require(key not in namespace, "selected basename/case collision")
        namespace[key] = name
        plan[name] = spec

    for row in rows:
        if str(PurePosixPath(row["relativePath"]).parent) not in STAGED_DIRS:
            continue
        name = row["name"]
        require(name.casefold() not in RESERVED | AUTHORED.keys(), "staged reserved/config collision")
        add(name, ("staged-local", row["relativePath"], row["bytes"], row["sha256"]))
    for row in pins:
        name = row["name"]
        if PurePosixPath(name).suffix not in LEGACY_EXTENSIONS:
            continue
        if name.casefold() in namespace:
            chosen = namespace[name.casefold()]
            require(chosen == name and plan[chosen][2:] == (row["length"], row["sha256"]),
                    "conflicting staged/legacy font identity")
            continue
        add(name, ("legacy-pinned-data", "files/" + name, row["length"], row["sha256"]))
    pins_by_name = {row["name"]: row for row in pins}
    for name, (length, digest) in ENGINES.items():
        require(name in pins_by_name and pins_by_name[name]["length"] == length
                and pins_by_name[name]["sha256"] == digest, "fixed engine pin mismatch")
    declared_total = sum(spec[2] for spec in plan.values()) + sum(len(b) for b in AUTHORED.values())
    declared_total += sum(length for length, _ in ENGINES.values())
    require(len(plan) + len(AUTHORED) + len(ENGINES) <= MAX_FILES, "selected count exceeds budget")
    require(declared_total <= MAX_TOTAL_BYTES, "selected total exceeds budget")
    # Schemas and all declared budgets passed before selected content reads.
    content, selection, engine_bytes = {}, {}, {}
    with ExitStack() as stack:
        staged_files = open_directory(stack, "files", staged_fd)
        legacy_files = open_directory(stack, "files", legacy_fd)
        for name, (origin, rel, length, digest) in sorted(plan.items()):
            fd, path = (staged_files, rel) if origin == "staged-local" else (legacy_files, name)
            data = read_bounded(fd, path, expected_size=length)
            require(hashlib.sha256(data).hexdigest() == digest, "selected content hash mismatch")
            content[name] = data
            selection[name] = {"logicalName": name, **identity(data), "origin": origin, "relativePath": rel}
        for name, data in AUTHORED.items():
            content[name] = data
            selection[name] = {"logicalName": name, **identity(data), "origin": "authored", "relativePath": "authored/" + name}
        for name, (length, digest) in ENGINES.items():
            data = read_bounded(legacy_files, name, expected_size=length)
            require(hashlib.sha256(data).hexdigest() == digest, "fixed engine content hash mismatch")
            engine_bytes[name] = data
    manifest = {
        "schemaVersion": 1, "candidateVersion": 9,
        "catalog": identity(catalog_raw), "pins": identity(pins_raw),
        "verifiedStagedFiles": sum(1 for spec in plan.values() if spec[0] == "staged-local"),
        "engine": {name: identity(data) for name, data in engine_bytes.items()},
        "selection": selection,
        "selectionPolicy": "Staged newer English kernel/packages/data; legacy .tfm/.pfb/.enc/.map only; authored pdflatex.ini and English-only hyphen.cfg; no legacy macros/config fallback",
    }
    return content, engine_bytes, manifest


def same_entry(fd, name, expected):
    try:
        info = os.stat(name, dir_fd=fd, follow_symlinks=False)
        return (info.st_dev, info.st_ino) == expected
    except FileNotFoundError:
        return False


def require_empty(fd):
    try:
        empty = not os.listdir(fd)
    except OSError as error:
        raise ValueError("output cannot be inspected") from error
    require(empty, "output must remain empty")


def emit(out_fd, content, engines, manifest):
    """Clean only successfully created, still-owned entries, never the root."""
    require_empty(out_fd)
    created = []
    files_fd = None
    directory_identity = None

    def write(fd, name, data):
        handle = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=fd)
        info = os.fstat(handle)
        created.append((fd, name, (info.st_dev, info.st_ino)))
        try:
            view = memoryview(data)
            while view:
                count = os.write(handle, view)
                require(count > 0, "short output write")
                view = view[count:]
        finally:
            os.close(handle)

    try:
        os.mkdir("files", dir_fd=out_fd)
        info = os.stat("files", dir_fd=out_fd, follow_symlinks=False)
        directory_identity = (info.st_dev, info.st_ino)
        files_fd = os.open("files", DIR_FLAGS, dir_fd=out_fd)
        for name, data in sorted(content.items()):
            write(files_fd, name, data)
        for name, data in sorted(engines.items()):
            write(out_fd, name, data)
        write(out_fd, "manifest.json", (json.dumps(manifest, indent=2, sort_keys=True) + "\n").encode())
    except BaseException:
        for fd, name, owned in reversed(created):
            if same_entry(fd, name, owned):
                os.unlink(name, dir_fd=fd)
        if directory_identity is not None and same_entry(out_fd, "files", directory_identity):
            try:
                os.rmdir("files", dir_fd=out_fd)
            except OSError:
                pass  # Never delete unrelated entries that appeared in it.
        raise
    finally:
        if files_fd is not None:
            os.close(files_fd)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--staged", required=True)
    parser.add_argument("--legacy", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    staged, legacy, out = (directory(value) for value in (args.staged, args.legacy, args.output))
    for root in (staged, legacy):
        require(not (root == out or root in out.parents or out in root.parents), "input/output ancestor overlap")
    with ExitStack() as stack:
        staged_fd = open_directory(stack, staged)
        legacy_fd = open_directory(stack, legacy)
        out_fd = open_directory(stack, out)
        require_empty(out_fd)
        content, engines, manifest = prepare(staged_fd, legacy_fd)
        emit(out_fd, content, engines, manifest)
    print(f"Prepared {len(content)} selected data files + {len(engines)} fixed engines; INITEX not invoked.")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, TypeError, UnicodeError, RecursionError):
        # No input JSON contents, arbitrary file contents or private paths printed.
        print("FAIL: invalid/unsafe inputs, budget, identity, output policy or IO failure", file=sys.stderr)
        sys.exit(1)
