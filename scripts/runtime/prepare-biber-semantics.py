#!/usr/bin/env python3
"""Reassemble exact P13 from already acquired/built, hash-locked inputs.

All input paths are explicit. --base is the accepted P12 pins.json/files tree;
--biber, --semantic, --converter and --lua are component directories. Format and
Lua-resource arguments are individual files. --notices is the native Biber source
root (not its notices subdirectory). Unknown directory content is never adopted.

This copies verified artifacts; native source builds remain in the recipes named
by biber-semantics.lock.json. It does not download, compile, adopt a default or
establish browser acceptance. Package separately with package-compiler-runtime.py.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import stat


def digest(data):
    return hashlib.sha256(data).hexdigest()


def resource_name(value):
    if (not isinstance(value, str) or not 1 <= len(value) <= 200
            or not re.fullmatch(r"[A-Za-z0-9_.\-/]+", value)
            or any(part in ("", ".", "..") or len(part) > 100 for part in value.split("/"))):
        raise ValueError("Unsafe declared resource path")
    return value


def safe_path(value, directory=None, fresh=False):
    path = Path(value)
    if ".." in path.parts:
        raise ValueError("Traversal in explicit input/output path")
    path = path.absolute()
    for part in (path, *path.parents):
        if part.is_symlink():
            raise ValueError("Symlink input/output is not supported")
    if fresh:
        if path.exists():
            raise ValueError("Output must be fresh; existing artifacts are immutable")
        if not path.parent.is_dir():
            raise ValueError("Fresh output parent must already exist")
        return path
    info = path.lstat()
    expected = stat.S_ISDIR if directory else stat.S_ISREG
    if not expected(info.st_mode):
        raise ValueError("Expected regular file or directory")
    return path


def overlap(a, b):
    if a == b or a in b.parents or b in a.parents:
        return True
    # Also detect the same directory exposed under separate bind-mount names.
    for existing, other in ((a, b), (b, a)):
        if existing.exists() and existing.is_dir():
            info = existing.stat()
            if any(parent.exists() and (parent.stat().st_dev, parent.stat().st_ino) == (info.st_dev, info.st_ino)
                   for parent in other.parents):
                return True
    return False


def read_checked(path, expected, maximum):
    path = safe_path(path)
    length = expected["length"]
    if (type(length) is not int or not 0 <= length <= maximum
            or not re.fullmatch(r"[0-9a-f]{64}", expected["sha256"])
            or path.stat().st_size != length):
        raise ValueError("Input length or declared digest mismatch: " + path.name)
    # Capture owned bytes before any output is created. A later source mutation
    # cannot change the verified copy or create a partially authenticated result.
    with path.open("rb") as stream:
        data = stream.read(length + 1)
    if len(data) != length or digest(data) != expected["sha256"]:
        raise ValueError("Input integrity mismatch: " + path.name)
    return data


def closure(rows):
    return digest("\n".join(f"{p['name']}\t{p['length']}\t{p['sha256']}" for p in rows).encode())


def generated_pins(lock, base_pins, target_pins):
    fields = lock["pinFields"]
    lines = ["namespace Client.Compilation.Native;", "",
             "/// <summary>Private closed P13 candidate; product acceptance/default adoption is separate.</summary>",
             "public static class NativeCompilerBiberPins", "{", "    public static bool Ready => true;",
             f"    public const int ResourceCount = {len(target_pins)}, TotalBytes = {lock['target']['totalBytes']};",
             f'    public const string ClosureHash = "{lock["target"]["closureSha256"]}";']
    for field, name in fields.items():
        pin = target_pins[name]
        lines.extend([f"    public const int {field}Length = {pin['length']};",
                      f'    public const string {field}Hash = "{pin["sha256"]}";'])
    lines.extend(["", "    public static bool MatchesComponents(IReadOnlyDictionary<string, NativeResourcePin> pins)",
                  "    {", "        var previous = new Dictionary<string, NativeResourcePin>(pins, StringComparer.Ordinal)", "        {"])
    changed = [entry["name"] for entry in lock["components"]]
    restore = set(changed).intersection(base_pins) | {"luatex.js", "luatex.wasm", "lualatex.fmt", "luaotfload-harf-plug.lua"}
    for name in sorted(restore):
        pin = base_pins[name]
        lines.append(f'            ["{name}"] = new("{name}", {pin["length"]}, "{pin["sha256"]}"),')
    lines.extend(["        };"])
    for name in changed:
        if name not in base_pins:
            lines.append(f'        previous.Remove("{name}");')
    lines.append("        return NativeCompilerUnicodePins.MatchesComponents(previous)")
    for index, (field, name) in enumerate(fields.items()):
        lines.append(f'            && Fixed("{name}", {field}Length, {field}Hash)' + (";" if index == len(fields)-1 else ""))
    lines.extend(['        bool Fixed(string name, int length, string hash) => pins.TryGetValue(name, out var p) && p.Length == length && p.Sha256 == hash;', "    }", "}"])
    return ("\n".join(lines)+"\n").encode()


def assemble(args):
    lock_path = Path(__file__).with_name("biber-semantics.lock.json")
    lock_bytes = safe_path(lock_path).read_bytes()
    lock = json.loads(lock_bytes)
    if lock["schemaVersion"] != 1 or lock["profile"] != 13:
        raise ValueError("Unsupported assembly lock")
    directories = {name: safe_path(getattr(args, name), directory=True)
                   for name in ("base", "biber", "semantic", "converter", "lua", "notices")}
    single_files = {name: safe_path(getattr(args, name)) for name in ("pdf_format", "dvi_format", "lua_resource")}
    output = safe_path(args.out, fresh=True)
    pin_output = safe_path(args.pins_output, fresh=True) if args.pins_output is not None else None
    roots = [*directories.values(), *single_files.values(), Path(__file__).absolute().parent]
    if any(overlap(output, path) or pin_output is not None and overlap(pin_output, path) for path in roots):
        raise ValueError("Input/output overlap is forbidden")
    if pin_output is not None and overlap(output, pin_output):
        raise ValueError("Review pins must be separate from runtime output")

    limits = lock["limits"]
    raw_pins = read_checked(directories["base"]/"pins.json", lock["base"]["pins"], limits["manifestBytes"])
    base_rows = json.loads(raw_pins)
    if not isinstance(base_rows, list) or len(base_rows) != lock["base"]["resourceCount"]:
        raise ValueError("Expected exact closed P12 base inventory")
    base_pins = {}
    for pin in base_rows:
        if set(pin) != {"name", "length", "sha256"}:
            raise ValueError("Invalid base pin fields")
        name = resource_name(pin["name"])
        if name in base_pins:
            raise ValueError("Duplicate base resource")
        base_pins[name] = pin
    if (list(base_pins) != sorted(base_pins) or closure(base_rows) != lock["base"]["closureSha256"]
            or sum(p["length"] for p in base_rows) != lock["base"]["totalBytes"]):
        raise ValueError("Mixed or unsorted P12 base inventory")

    # Non-base checks first: corrupted small candidate/notice inputs fail without
    # reading/capturing the entire 229MB base. Read only declared source names.
    replacements = {}
    for entry in lock["components"]:
        name = resource_name(entry["name"])
        group, source = entry["group"], entry["source"]
        path = single_files[group] if group in single_files else directories[group]/resource_name(source)
        if name in replacements:
            raise ValueError("Duplicate replacement")
        replacements[name] = ({k: entry[k] for k in ("name", "length", "sha256")}, read_checked(path, entry, limits["fileBytes"]))
    notice_names = set()
    for entry in lock["notices"]:
        name = resource_name(entry["name"])
        if not name.startswith("notices/biber/") or name in base_pins or name in replacements:
            raise ValueError("Notice namespace collision")
        notice_names.add(name)
        path = directories["notices"]/resource_name(entry["source"])
        replacements[name] = ({k: entry[k] for k in ("name", "length", "sha256")}, read_checked(path, entry, limits["fileBytes"]))
    if len(notice_names) != lock["noticeCount"] or sum(replacements[n][0]["length"] for n in notice_names) != lock["noticeBytes"]:
        raise ValueError("Incomplete original notice closure")
    owned = {}
    for pin in base_rows:
        name = pin["name"]
        # Replaced base inputs are still verified: a mixed base cannot be hidden
        # by supplying an accepted replacement over its corrupted resource.
        data = read_checked(directories["base"]/"files"/name, pin, limits["fileBytes"])
        if name not in replacements:
            owned[name] = (pin, data)
    owned.update(replacements)
    rows = [owned[name][0] for name in sorted(owned)]
    total = sum(pin["length"] for pin in rows)
    if (len(rows) != lock["target"]["resourceCount"] or len(rows) > limits["resourceCount"]
            or total != lock["target"]["totalBytes"] or total > limits["totalBytes"]
            or closure(rows) != lock["target"]["closureSha256"]):
        raise ValueError("Incomplete or mixed P13 target closure")
    for name in lock["retainedBibtex"]:
        if owned[name][0] != base_pins[name]:
            raise ValueError("Accepted BibTeX executable must remain unchanged")
    manifest = (json.dumps(rows, indent=2)+"\n").encode()
    if len(manifest) != lock["target"]["pins"]["length"] or digest(manifest) != lock["target"]["pins"]["sha256"]:
        raise ValueError("Target pins serialization differs from accepted closure")
    review_pins = generated_pins(lock, base_pins, dict((p["name"], p) for p in rows))
    receipt = {"schemaVersion": 1, "profile": 13, "status": "exact artifact reassembly; no default adoption or browser acceptance",
               "resourceCount": len(rows), "totalBytes": total, "closureSha256": closure(rows),
               "baseClosureSha256": lock["base"]["closureSha256"], "pinsSha256": digest(manifest),
               "assemblyLockSha256": digest(lock_bytes), "biberNoticeCount": len(notice_names), "biberNoticeBytes": lock["noticeBytes"],
               "phases": lock["phases"], "retainedBibtex": lock["retainedBibtex"],
               "sourceBuildAssociations": lock["sourceBuildAssociations"],
               "nativeBuildPerformed": False, "networkUsed": False,
               "bootstrap": "Biber .data is a separate authenticated bootstrap, excluded from TeX/converter inputs",
               "scope": "Only declared input names were adopted. All bytes captured and checked before creating either output. Original GPL/AGPL and other notices preserved verbatim."}
    output.mkdir()
    for name, (_, data) in owned.items():
        destination = output/"files"/name
        destination.parent.mkdir(parents=True, exist_ok=True)
        with destination.open("xb") as stream:
            stream.write(data)
    (output/"pins.json").write_bytes(manifest)
    (output/"profile13-staging.json").write_text(json.dumps(receipt, indent=2)+"\n")
    if pin_output is not None:
        with pin_output.open("xb") as stream:
            stream.write(review_pins)
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for flag in ("base", "biber", "semantic", "pdf-format", "dvi-format", "converter", "lua", "lua-resource", "notices", "out"):
        parser.add_argument("--"+flag, type=Path, required=True)
    parser.add_argument("--pins-output", type=Path, help="optional fresh C# file for review; never overwrites product source")
    args = parser.parse_args()
    try:
        print(json.dumps(assemble(args)))
    except (ValueError, OSError, KeyError, TypeError) as error:
        parser.exit(2, "Refused P13 assembly: "+str(error)+"\n")


if __name__ == "__main__":
    main()
