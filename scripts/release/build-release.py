#!/usr/bin/env python3
"""R4 A1: bounded, read-only release-material verification; no builds or downloads."""
import argparse
from collections.abc import Iterator
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import stat
import sys
import tarfile
import time
from typing import Any
import unicodedata
import zipfile

METADATA_BYTES = 16 * 1024 * 1024
MAX_ROWS = 200_000
SAFE_INT = 2**53 - 1
CHUNK = 64 * 1024
CONFIGURATION = "Release;PublishTrimmed=false;CompressionEnabled=false"
SCHEMA_PATH = Path(__file__).with_name("release-schema.json")


class Refusal(Exception):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


def require(condition, message, code=2):
    if not condition:
        raise Refusal(code, message)


def ordinal(value):
    """Match .NET StringComparer.Ordinal: UTF-16 code units, no normalization."""
    return value.encode("utf-16-be", "strict") if isinstance(value, str) else value


def canonical(value):
    def ordered(item):
        if isinstance(item, dict):
            return {k: ordered(item[k]) for k in sorted(item, key=ordinal)}
        if isinstance(item, list):
            return [ordered(v) for v in item]
        return item
    try:
        return (json.dumps(ordered(value), ensure_ascii=False,
                           separators=(",", ":"), allow_nan=False) + "\n").encode("utf-8")
    except (ValueError, UnicodeError):
        raise Refusal(2, "invalid JSON encoding or number") from None


def path_name(value):
    require(isinstance(value, str) and bool(value), "empty or non-string path")
    try:
        size = len(value.encode("utf-8", "strict"))
    except UnicodeError:
        raise Refusal(2, "invalid path Unicode") from None
    require(size <= 1024 and unicodedata.normalize("NFC", value) == value,
            "path length or NFC violation")
    require(not any(unicodedata.category(c) in ("Cc", "Cf", "Cs") for c in value),
            "control character in path")
    require(not any(c in value for c in "\\:"), "backslash or colon in path")
    require(all(p not in ("", ".", "..") for p in value.split("/")),
            "absolute, empty or traversal path segment")
    return value


def unique_paths(paths):
    seen, prefixes, directories = set(), {}, set()
    for name in paths:
        path_name(name)
        key = name.casefold()
        require(key not in seen, "duplicate/case/NFC path alias")
        parts = name.split("/")
        for index in range(1, len(parts) + 1):
            prefix = "/".join(parts[:index])
            alias = prefix.casefold()
            require(prefixes.get(alias, prefix) == prefix, "case alias in path ancestor")
            require(index == len(parts) or alias not in seen, "file/directory path alias")
            if index < len(parts):
                directories.add(alias)
            prefixes[alias] = prefix
        require(key not in directories, "directory/file path alias")
        seen.add(key)


def no_host_links(path):
    """Check even ancestors above the root; never resolve a supplied symlink."""
    absolute = Path(os.path.abspath(path))
    for part in reversed([absolute, *absolute.parents]):
        try:
            mode = part.lstat().st_mode
        except FileNotFoundError:
            continue
        require(not stat.S_ISLNK(mode), "symlink input/output path")
    return absolute


class Budget:
    def __init__(self, cancel=None, deadline=None):
        self.cancel = cancel or (lambda: False)
        self.deadline = deadline
        self.rows = 0

    def check(self):
        if self.cancel():
            raise Refusal(130, "cancelled; no output written")
        if self.deadline is not None and time.monotonic() >= self.deadline:
            raise Refusal(124, "deadline; no output written")

    def count(self, count):
        self.check()
        self.rows += count
        require(self.rows <= MAX_ROWS, "metadata exceeds 200000 aggregate rows")


def load_json(data, budget, canonical_required=True):
    require(len(data) <= METADATA_BYTES, "metadata exceeds 16 MiB")

    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, "duplicate JSON field")
            result[key] = value
        return result

    def number(text):
        try:
            value = int(text)
        except ValueError:
            raise Refusal(2, "invalid or oversized JSON integer") from None
        require(abs(value) <= SAFE_INT, "integer overflow")
        return value

    def no_float(_):
        raise Refusal(2, "non-integer/nonfinite JSON number")

    try:
        value = json.loads(data.decode("utf-8", "strict"), object_pairs_hook=pairs,
                           parse_int=number, parse_float=no_float, parse_constant=no_float)
    except (UnicodeError, ValueError, RecursionError):
        raise Refusal(2, "invalid UTF-8 JSON or nesting") from None

    def walk(item, depth=0):
        require(depth <= 64, "metadata nesting exceeds 64")
        budget.check()
        if isinstance(item, list):
            budget.count(len(item))
            for entry in item:
                walk(entry, depth + 1)
        elif isinstance(item, dict):
            for key, entry in item.items():
                walk(key, depth + 1)
                walk(entry, depth + 1)
        elif isinstance(item, str):
            try:
                item.encode("utf-8", "strict")
            except UnicodeError:
                raise Refusal(2, "invalid Unicode string") from None
            require(not any(unicodedata.category(c) in ("Cc", "Cf", "Cs") for c in item),
                    "control character in metadata string")
    walk(value)
    if canonical_required:
        require(data == canonical(value), "JSON must use ordinal compact canonical bytes and LF")
    return value


class Schema:
    """Small explicit interpreter for ONLY the keywords used by the pinned schema.

    No remote refs, dynamic code, network, third-party dependency or silent keyword
    ignoring. JSON Schema remains consumable by an independent full validator.
    """
    KEYWORDS = {"$schema", "$id", "$defs", "title", "$ref", "type", "const", "enum",
                "required", "properties", "additionalProperties", "items", "anyOf",
                "minLength", "maxLength", "minItems", "maxItems", "minimum", "maximum",
                "pattern", "format", "x-sortBy"}

    def __init__(self):
        self.root = load_json(SCHEMA_PATH.read_bytes(), Budget(), canonical_required=False)
        self.audit(self.root)

    def audit(self, rule):
        require(not (set(rule) - self.KEYWORDS), "unsupported schema keyword")
        for key in ("properties", "$defs"):
            for child in rule.get(key, {}).values():
                self.audit(child)
        for key in ("items",):
            if key in rule:
                self.audit(rule[key])
        for child in rule.get("anyOf", []):
            self.audit(child)

    def validate(self, value: Any, rule=None):
        if isinstance(rule, str):
            rule = self.root["$defs"][rule]
        if rule is None:
            rule = self.root
        if "$ref" in rule:
            ref = rule["$ref"]
            require(ref.startswith("#/$defs/"), "unsupported schema reference")
            return self.validate(value, ref.removeprefix("#/$defs/"))
        if "anyOf" in rule:
            for option in rule["anyOf"]:
                try:
                    self.validate(value, option)
                    return
                except Refusal:
                    pass
            raise Refusal(2, "value matches no schema alternative")
        if "const" in rule:
            require(type(value) is type(rule["const"]) and value == rule["const"],
                    "wrong constant/version")
        if "enum" in rule:
            require(value in rule["enum"] and not isinstance(value, (dict, list, bool)),
                    "unknown enum")
        kind = rule.get("type")
        types = {"object": dict, "array": list, "string": str, "integer": int,
                 "null": type(None)}
        if kind:
            require(type(value) is types[kind], "wrong field type")
        if kind == "object":
            if not isinstance(value, dict):
                raise Refusal(2, "wrong object type")
            require(set(rule.get("required", [])) <= set(value), "missing required field", 3)
            require(not rule.get("additionalProperties", True), "schema object not closed")
            require(set(value) <= set(rule.get("properties", {})), "unknown field")
            for key, child in value.items():
                self.validate(child, rule["properties"][key])
        elif kind == "array":
            if not isinstance(value, list):
                raise Refusal(2, "wrong array type")
            require(rule.get("minItems", 0) <= len(value) <= rule.get("maxItems", MAX_ROWS),
                    "array size violation")
            for entry in value:
                self.validate(entry, rule.get("items", {}))
            if "x-sortBy" in rule:
                def field(item, key):
                    for part in key.split("."):
                        item = item[part]
                    return item
                keys = rule["x-sortBy"]
                order = [tuple(ordinal(field(x, k)) for k in keys) if keys else ordinal(x) for x in value]
                require(order == sorted(order) and len(set(order)) == len(order),
                        "array not ordinal sorted/unique")
        elif kind == "string":
            if not isinstance(value, str):
                raise Refusal(2, "wrong string type")
            require(rule.get("minLength", 0) <= len(value) <= rule.get("maxLength", METADATA_BYTES),
                    "string size violation")
            if "pattern" in rule:
                require(re.fullmatch(rule["pattern"], value) is not None, "invalid name/hash")
            if rule.get("format") == "release-path":
                path_name(value)
            elif rule.get("format") == "utc-time":
                require(re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ", value),
                        "time must be UTC RFC3339 whole seconds")
                try:
                    dt.datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ")
                except ValueError:
                    raise Refusal(2, "invalid UTC time") from None
        elif kind == "integer":
            if not isinstance(value, int):
                raise Refusal(2, "wrong integer type")
            require(rule.get("minimum", 0) <= value <= rule.get("maximum", SAFE_INT),
                    "integer outside bounds")


class InputTree:
    def __init__(self, root, budget):
        self.root = no_host_links(root)
        self.budget = budget
        flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
        fd = os.open("/", flags)
        try:
            for part in self.root.parts[1:]:
                budget.check()
                nxt = os.open(part, flags, dir_fd=fd)
                os.close(fd)
                fd = nxt
            self.fd = os.dup(fd)
        except FileNotFoundError:
            raise Refusal(3, "missing input root") from None
        except OSError:
            raise Refusal(2, "unsafe or unreadable input root") from None
        finally:
            os.close(fd)

    def close(self):
        os.close(self.fd)

    def open(self, name):
        path_name(name)
        fd = os.dup(self.fd)
        try:
            parts = name.split("/")
            for part in parts[:-1]:
                nxt = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=fd)
                os.close(fd)
                fd = nxt
            result = os.open(parts[-1], os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK, dir_fd=fd)
            info = os.fstat(result)
            if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
                os.close(result)
                raise Refusal(2, "non-regular or hardlinked input")
            return os.fdopen(result, "rb")
        except FileNotFoundError:
            raise Refusal(3, "missing declared input") from None
        except OSError:
            raise Refusal(2, "unsafe or unreadable input path") from None
        finally:
            os.close(fd)

    def read(self, name):
        with self.open(name) as stream:
            require(os.fstat(stream.fileno()).st_size <= METADATA_BYTES, "metadata exceeds 16 MiB")
            data = stream.read(METADATA_BYTES + 1)
            self.budget.check()
            require(len(data) <= METADATA_BYTES, "metadata exceeds 16 MiB")
            return data

    def hash(self, fact):
        self.budget.check()
        with self.open(fact["path"]) as stream:
            before = os.fstat(stream.fileno())
            require(before.st_size == fact["length"], "file byte length mismatch", 4)
            digest = hashlib.sha256()
            length = 0
            while True:
                self.budget.check()
                block = stream.read(CHUNK)
                if not block:
                    break
                length += len(block)
                require(length <= fact["length"], "file grew while hashing", 4)
                digest.update(block)
            after = os.fstat(stream.fileno())
            require((before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns, before.st_ctime_ns)
                    == (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns, after.st_ctime_ns),
                    "file changed while hashing", 4)
            require(length == fact["length"] and digest.hexdigest() == fact["sha256"],
                    "file SHA-256 mismatch", 4)

    def entries(self):
        result = []
        entry_count = 0
        node_aliases = {}

        def scan(fd, prefix):
            nonlocal entry_count
            with os.scandir(fd) as entries:
                for entry in entries:
                    self.budget.check()
                    entry_count += 1
                    require(entry_count <= MAX_ROWS + 1, "input tree exceeds entry limit")
                    name = prefix + entry.name
                    path_name(name)
                    alias = name.casefold()
                    require(node_aliases.get(alias, name) == name, "case alias in input tree")
                    node_aliases[alias] = name
                    require(not entry.is_symlink(), "symlink in input tree")
                    if entry.is_dir(follow_symlinks=False):
                        child = os.open(entry.name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
                                        dir_fd=fd)
                        try:
                            scan(child, name + "/")
                        finally:
                            os.close(child)
                    else:
                        require(entry.is_file(follow_symlinks=False), "special file in input tree")
                        result.append(name)
                        require(len(result) <= MAX_ROWS + 1, "input tree exceeds row limit")
        try:
            scan(self.fd, "")
        except OSError:
            raise Refusal(2, "input tree changed or unreadable") from None
        unique_paths(result)
        return set(result)


def known_text(value):
    return isinstance(value, str) and bool(value.strip()) and value.strip().casefold() not in {
        "unknown", "pending", "tbd", "todo", "none", "noassertion", "unestablished",
        "noncommercial", "non-commercial", "free/noncommercial"}


def facts(value: Any) -> Iterator[dict[str, Any]]:
    if isinstance(value, dict):
        if set(value) == {"path", "length", "sha256"}:
            yield value
        else:
            for entry in value.values():
                yield from facts(entry)
    elif isinstance(value, list):
        for entry in value:
            yield from facts(entry)


def paths(items):
    return {item["path"] for item in items}


def acyclic(edges, label, budget):
    """Iterative DFS: large hostile graphs cannot exhaust Python recursion."""
    done = set()
    for start in edges:
        active = set()
        stack = [(start, False)]
        while stack:
            budget.check()
            node, exit_node = stack.pop()
            if exit_node:
                active.remove(node)
                done.add(node)
            elif node not in done:
                require(node not in active, label + " cycle")
                active.add(node)
                stack.append((node, True))
                stack.extend((child, False) for child in edges.get(node, ()))


class Verifier:
    def __init__(self, tree, schema, manifest, manifest_name):
        self.tree, self.schema, self.m = tree, schema, manifest
        self.manifest_name = manifest_name
        self.budget = tree.budget
        self.inventory = {f["path"]: f for f in manifest["files"]}
        self.documents = {}
        self.validations = set()
        self.edges = {}
        self.pending = []

    def refs(self, value):
        for fact in facts(value):
            require(fact["path"] != self.manifest_name, "root manifest hash cycle")
            require(fact["path"] in self.inventory, "undeclared FileFact", 3)
            require(self.inventory[fact["path"]] == fact, "conflicting FileFact association", 4)

    def doc(self, fact, kind):
        self.refs(fact)
        name = fact["path"]
        self.budget.check()
        if name in self.documents:
            value = self.documents[name]
        else:
            value = load_json(self.tree.read(name), self.budget)
            self.documents[name] = value
        if (name, kind) not in self.validations:
            self.schema.validate(value, kind)
            self.validations.add((name, kind))
        if name not in self.edges:
            self.refs(value)
            self.edges[name] = paths(facts(value))
        return value

    def file_manifest(self, fact):
        result = self.doc(fact, "fileManifest")["files"]
        unique_paths(f["path"] for f in result)
        return result

    def distribution(self, condition, reason):
        if not condition:
            self.pending.append(reason)

    def verify(self):
        unique_paths(self.inventory)
        require(self.manifest_name not in self.inventory, "root manifest cannot inventory itself")
        actual = self.tree.entries()
        require(actual == set(self.inventory) | {self.manifest_name},
                "missing or extra input/material/executable", 3 if set(self.inventory) - actual else 2)
        for fact in self.m["files"]:
            self.tree.hash(fact)
        self.refs({k: v for k, v in self.m.items() if k not in ("files", "archives")})
        for archive in self.m["archives"]:
            self.refs(archive["file"])
        release = self.m["release"]
        source_files = self.file_manifest(release["source"]["treeManifest"])
        require(bool(source_files), "empty source tree")
        require(release["source"]["treeSha256"] == release["source"]["treeManifest"]["sha256"],
                "source tree digest mismatch", 4)
        lock = self.doc(self.m["buildInputs"], "buildInputs")
        self.verify_lock(lock)
        self.verify_compiler()
        self.verify_components()
        self.verify_stages(lock)
        self.verify_application()
        self.verify_archives()
        acyclic(self.edges, "FileFact", self.budget)
        reachable = set()
        todo = list(f["path"] for f in facts({k: v for k, v in self.m.items()
                                            if k not in ("files", "archives")}))
        todo += [a["file"]["path"] for a in self.m["archives"]]
        while todo:
            self.budget.check()
            name = todo.pop()
            if name not in reachable:
                reachable.add(name)
                todo.extend(self.edges.get(name, ()))
        require(reachable == set(self.inventory), "extra unassociated input/material/executable")
        # Rehash after semantic/streamed archive checks; do not accept a changed input.
        for fact in self.m["files"]:
            self.tree.hash(fact)
        require(self.tree.entries() == actual, "input tree changed during validation", 4)
        require(not self.pending, "distribution-pending: " + "; ".join(sorted(set(self.pending))), 7)
        return {"schemaVersion": 1, "status": "pass", "scope": "input-integrity-only",
                "files": len(self.inventory), "sourceTreeSha256": release["source"]["treeSha256"],
                "candidateIdentitySha256": hashlib.sha256(canonical(release)).hexdigest(),
                "limitations": "No build, legal clearance, runtime admission or publication acceptance."}

    def verify_lock(self, lock):
        release = self.m["release"]
        build = release["build"]
        for key in ("recipeManifest", "toolchainManifest", "dependencyManifest",
                    "sourceDateEpoch", "configuration"):
            require(lock[key] == build[key], "stale build lock/recipe/toolchain/configuration", 4)
        require(lock["sourceTreeSha256"] == release["source"]["treeSha256"], "stale source lock", 4)
        require(lock["approvedProfileSha256"] == self.m["approvedProfileIdentity"]["sha256"],
                "stale approved-profile lock", 4)
        for key in ("recipeManifest", "toolchainManifest", "dependencyManifest"):
            self.file_manifest(build[key])
        self.locked_sources = paths(self.file_manifest(release["source"]["treeManifest"]))
        self.locked_sources |= paths(self.file_manifest(build["dependencyManifest"]))

    def verify_compiler(self):
        compiler = self.m["release"]["compiler"]
        approved = self.doc(self.m["approvedProfileIdentity"], "compiler")
        require(approved == compiler, "unaccepted profile/component/phase tuple", 4)
        # These are descriptive known semantic pairs, NOT an app allowlist/admission update.
        engine_phases = {"pdftex": (6, 11), "latex-dvi": (7, 12),
                         "xelatex": (8, 13), "lualatex": (9, 14)}
        roles = {"pdftex": {"latex": "compile", "bibtex": "compile"},
                 "latex-dvi": {"latex": "compile", "bibtex": "compile", "dvipdfmx": "dvi"},
                 "xelatex": {"latex": "xetex", "bibtex": "compile", "dvipdfmx": "dvi"},
                 "lualatex": {"latex": "luatex", "bibtex": "compile"}}
        components = {c["componentId"]: c for c in compiler["components"]}
        for row in compiler["admittedTuples"]:
            require(row["profile"] == compiler["profile"] and row["componentId"] in components,
                    "tuple profile/component mismatch")
            if row["engineId"] in engine_phases:
                if row["phase"] == 10:
                    # R1: bibliography backend changes tool/role, NOT the caller
                    # engine identity. Phase10 is exclusively genuine Biber.
                    require(row["tool"] == "biber" and row["workerRole"] == "biber"
                            and components[row["componentId"]]["role"] == "bibliography",
                            "unsupported Biber tool/role/component tuple")
                else:
                    require(row["phase"] in engine_phases[row["engineId"]]
                            and row["workerRole"] == roles[row["engineId"]].get(row["tool"]),
                            "unsupported engine role/phase tuple")
            else:
                # No invented converter/query/PDF phase numbers in A1. Root must add
                # the accepted typed contract before such a final tuple can pass.
                raise Refusal(2, "missing accepted auxiliary role/phase contract")
            require(row["tool"] in ("latex", "bibtex", "biber", "dvipdfmx"), "unsupported phase tool")
            require((row["tool"] == "biber") == (row["phase"] == 10),
                    "Biber cannot be a BibTeX fallback or use a TeX phase")
            require(components[row["componentId"]]["role"] != "bibliography"
                    or row["tool"] == "biber", "bibliography component cannot be relabelled as TeX/BibTeX")
            # BibTeX tags retain the selected engine/phase, but MUST name the
            # approved pdfTeX executable, never the Xe/Lua executable.
            if row["tool"] == "bibtex":
                pdf_components = {t["componentId"] for t in compiler["admittedTuples"]
                                  if t["engineId"] == "pdftex" and t["tool"] == "latex"}
                require(row["componentId"] in pdf_components, "BibTeX lacks accepted pdfTeX component")
            if row["tool"] == "dvipdfmx":
                require(components[row["componentId"]]["role"] == "converter",
                        "conversion lacks accepted converter component")
        inventory = self.file_manifest(compiler["inventory"])
        require(bool(inventory), "empty compiler inventory")
        archive = [a for a in self.m["archives"] if a["file"] == compiler["zip"]]
        require(len(archive) == 1, "compiler ZIP lacks exact declared archive members", 3)
        members = archive[0]["members"]
        require(len(members) == len(inventory), "compiler archive/member count mismatch", 4)
        require(bool(members), "compiler archive has no members", 3)
        first = members[0]
        matches = []
        for candidate in inventory:
            self.budget.check()
            if (candidate["path"] == first["path"] or candidate["path"].endswith("/" + first["path"])) \
                    and (candidate["length"], candidate["sha256"]) == (first["length"], first["sha256"]):
                matches.append(candidate)
        require(len(matches) == 1, "compiler archive/member association mismatch", 4)
        prefix = matches[0]["path"][:-len(first["path"])]
        by_path = {f["path"]: f for f in inventory}
        for member in members:
            self.budget.check()
            expected = {**member, "path": prefix + member["path"]}
            require(by_path.get(expected["path"]) == expected,
                    "compiler archive/member association mismatch", 4)
        closure = "\n".join(f"{f['path']}\t{f['length']}\t{f['sha256']}" for f in members)
        require(hashlib.sha256(closure.encode("utf-8")).hexdigest() == compiler["closureSha256"],
                "compiler closure mismatch (existing ordinal name/tab/length/tab/hash contract)", 4)

    def verify_components(self):
        release = self.m["release"]
        rows = self.doc(release["materials"]["componentInventory"], "inventory")["components"]
        comps = {c["componentId"]: c for c in rows}
        notices_doc = self.doc(release["materials"]["noticesIndex"], "notices")
        notices = {n["id"]: n for n in notices_doc["notices"]}
        works = {w["workId"]: w for w in notices_doc["works"]}
        decision = self.doc(release["materials"]["licenseDecision"], "decision")
        runtime_ids = {c["componentId"] for c in rows if c["role"] == "runtime"}
        require(set(decision["components"]) == runtime_ids, "license decision component mismatch")
        self.distribution(decision["status"] == "approved" and known_text(decision["policy"])
                          and decision["reviewReference"] is not None, "project policy/decision missing")
        shipped = {}
        for row in rows:
            cid = row["componentId"]
            require(set(row["generatedFrom"]) <= set(comps), "missing generated component dependency", 3)
            sources = self.file_manifest(row["sourceFilesManifest"])
            require(bool(sources), "missing component source association", 3)
            source_paths = paths(sources)
            require(source_paths <= self.locked_sources, "component source not in source/dependency lock", 4)
            if row["role"] == "build-only":
                require(not row["shippedFiles"], "build-only component is shipped")
            for fact in row["shippedFiles"]:
                require(fact["path"] not in shipped, "ambiguous shipped component association")
                shipped[fact["path"]] = cid
            self.distribution(row["provenanceStatus"] == "complete" and known_text(row["upstreamIdentity"]),
                              "unknown provenance: " + cid)
            self.distribution(row["principalTextStatus"] == "present" and bool(row["licenseEvidence"])
                              and known_text(row["licenseExpression"]) and row["distributionStatus"] == "approved"
                              and row["reviewDecision"] is not None, "unreviewed license/text: " + cid)
            if row["reviewDecision"] is not None:
                review = self.doc(row["reviewDecision"], "decision")
                require(cid in review["components"], "wrong review decision association")
                self.distribution(review["status"] == "approved" and known_text(review["policy"])
                                  and review["reviewReference"] is not None, "pending component review: " + cid)
            build = self.doc(row["buildReceipt"], "componentBuild")
            require(build["componentId"] == cid and build["sourceManifest"] == row["sourceFilesManifest"]
                    and build["selectedLinkManifest"] == row["selectedLinkManifest"]
                    and build["outputs"] == row["shippedFiles"]
                    and build["inputManifestSha256"] == self.m["buildInputs"]["sha256"],
                    "wrong component source/build/output association", 4)
            patch_doc = self.doc(build["patchManifest"], "patches")
            require([p["patch"] for p in patch_doc["patches"]] == row["patches"],
                    "patch input/preimage association mismatch", 4)
            for patch in patch_doc["patches"]:
                require(patch["path"] == patch["after"]["path"]
                        and patch["after"]["path"] in source_paths, "patch postimage not in component source")
            require(row["selectedLinkManifest"] is not None or not row["shippedFiles"],
                    "missing selected-link/member association", 3)
            members = []
            if row["selectedLinkManifest"]:
                link = self.doc(row["selectedLinkManifest"], "link")
                require(link["componentId"] == cid, "wrong selected-link component")
                members = link["members"]
                for member in members:
                    require(member["path"] == member["member"]["path"] and bool(member["sources"])
                            and paths(member["sources"]) <= source_paths and bool(member["workIds"])
                            and set(member["workIds"]) <= set(works), "wrong selected member/source/Work association")
                    for wid in member["workIds"]:
                        require(cid in works[wid]["componentIds"]
                                and paths(member["sources"]) <= paths(works[wid]["sourceFiles"]),
                                "member source not covered by Work")
            associated = [n for n in notices.values() if n["componentId"] == cid]
            for notice in associated:
                require(notice["workId"] in works and notice["source"]["path"] in source_paths,
                        "notice source/Work association mismatch")
                require(notice["text"] in row["licenseEvidence"], "notice absent from component license evidence")
                work = works[notice["workId"]]
                require(cid in work["componentIds"] and notice["id"] in work["noticeIds"]
                        and notice["source"] in work["sourceFiles"], "wrong notice Work/component")
                if notice["member"] is not None:
                    require(any(m["member"] == notice["member"] and notice["source"] in m["sources"]
                                and notice["workId"] in m["workIds"] for m in members),
                            "notice selected-member association mismatch")
            require(paths(row["licenseEvidence"]) == paths(n["text"] for n in associated),
                    "unassociated component license evidence")
            self.distribution(bool(associated) and any(n["kind"] == "complete" for n in associated),
                              "missing complete principal text: " + cid)
            require(source_paths <= {f["path"] for w in works.values() if cid in w["componentIds"]
                                     for f in w["sourceFiles"]}, "source missing Work association", 3)
        for work in works.values():
            require(set(work["componentIds"]) <= set(comps) and bool(work["componentIds"])
                    and set(work["noticeIds"]) <= set(notices) and bool(work["noticeIds"])
                    and set(work["generatedFrom"]) <= set(works), "dangling Work association")
            for cid in work["componentIds"]:
                require(paths(work["sourceFiles"]) <= paths(self.file_manifest(comps[cid]["sourceFilesManifest"])),
                        "Work includes source outside associated component")
            for nid in work["noticeIds"]:
                require(notices[nid]["workId"] == work["workId"], "Work notice reverse association mismatch")
        require({n["componentId"] for n in notices.values()} <= set(comps), "notice unknown component")
        acyclic({cid: row["generatedFrom"] for cid, row in comps.items()}, "component", self.budget)
        acyclic({wid: row["generatedFrom"] for wid, row in works.items()}, "Work", self.budget)
        for component in release["compiler"]["components"]:
            cid = component["componentId"]
            require(cid in comps and comps[cid]["role"] == "runtime", "compiler component not in runtime inventory", 3)
            row = comps[cid]
            build = self.doc(row["buildReceipt"], "componentBuild")
            require(component["sourceManifest"] == row["sourceFilesManifest"]
                    and component["patchManifest"] == build["patchManifest"]
                    and component["selectedLinkManifest"] == row["selectedLinkManifest"],
                    "compiler component material mismatch", 4)
            assoc = self.doc(component["noticeAssociation"], "association")
            require(assoc["componentId"] == cid
                    and set(assoc["noticeIds"]) == {n["id"] for n in notices.values() if n["componentId"] == cid}
                    and set(assoc["workIds"]) == {w["workId"] for w in works.values() if cid in w["componentIds"]},
                    "compiler notice association mismatch")
            require(any(key in component for key in ("loader", "wasm")), "compiler component lacks executable")
            for key in ("loader", "wasm"):
                if key in component:
                    require(component[key] in row["shippedFiles"], "compiler executable not associated")
        for fmt in release["compiler"]["formats"]:
            require(fmt["componentId"] in comps and fmt["engineComponentId"] in comps,
                    "format/engine component missing", 3)
            row = comps[fmt["componentId"]]
            require(fmt["output"] in row["shippedFiles"] and fmt["engineComponentId"] in row["generatedFrom"],
                    "format engine/derivation mismatch")
            inputs = self.file_manifest(fmt["inputManifest"])
            require(bool(inputs) and paths(inputs) == paths(self.file_manifest(row["sourceFilesManifest"])),
                    "format embedded source/Work inventory incomplete")
            require(fmt["recipe"] in self.file_manifest(release["build"]["recipeManifest"]),
                    "format recipe outside pinned recipe manifest", 4)
            environment = self.file_manifest(fmt["generationEnvironment"])
            require(bool(environment) and paths(environment) <= paths(self.file_manifest(
                release["build"]["toolchainManifest"])), "format generation environment not pinned", 4)
        access = self.doc(release["materials"]["sourceAccess"], "sourceAccess")["components"]
        require({a["componentId"] for a in access} == runtime_ids, "missing/extra recipient source route", 3)
        for entry in access:
            row = comps[entry["componentId"]]
            self.distribution(entry["route"] == "accompanying" and entry["decision"] == row["reviewDecision"],
                              "pending recipient source access: " + entry["componentId"])
            require(paths(self.file_manifest(row["sourceFilesManifest"])) <= paths(entry["files"]),
                    "recipient source inaccessible/incomplete", 3)
        self.shipped = shipped

    def verify_stages(self, lock):
        release = self.m["release"]
        stage_facts = release["build"]["stageReceipts"]
        stages = {}
        run_ids, output_ids = set(), set()
        for fact in stage_facts:
            row = self.doc(fact, "stage")
            key = (row["replica"], row["stage"])
            require(key not in stages, "duplicate stage/replica")
            require(row["id"] == "R4-BUILD-" + row["stage"] + "-" + row["replica"],
                    "wrong stage acceptance ID")
            require(row["status"] == "pass" and row["exitCode"] == 0, "external stage did not pass", 5)
            require(row.get("skipCount", 0) == 0 and not row.get("skips", []),
                    "required stage skipped cases cannot pass", 5)
            require(not row.get("reusedEvidence", []), "fresh build stage cannot reuse old output evidence", 5)
            require("releaseIdentitySha256" not in row,
                    "stage receipt cannot hash final identity; use external index/candidate join")
            require(row["sourceTreeSha256"] == lock["sourceTreeSha256"]
                    and row["inputManifestSha256"] == self.m["buildInputs"]["sha256"]
                    and row["toolchainManifest"] == lock["toolchainManifest"], "mixed/stale stage inputs", 4)
            require(row["startedUtc"] <= row["finishedUtc"], "stage time order invalid")
            preservation = self.doc(row["sourcePreservation"], "preservation")
            require(preservation["beforeSha256"] == preservation["afterSha256"] == lock["sourceTreeSha256"],
                    "source changed during stage", 4)
            fresh = row["freshBuild"]
            require(fresh["runId"] not in run_ids and fresh["outputRootId"] not in output_ids,
                    "reused stage run/output root")
            run_ids.add(fresh["runId"])
            output_ids.add(fresh["outputRootId"])
            prefix = "replicas/" + row["replica"] + "/" + row["stage"] + "/"
            require(all(f["path"].startswith(prefix) for f in row["outputs"]), "output outside fresh stage root")
            require({f["path"][len(prefix):] for f in row["outputs"]} == set(lock["stageOutputs"][row["stage"]]),
                    "missing/extra declared stage outputs", 3)
            stages[key] = (row, fact)
        require(set(stages) == {(r, s) for r in ("A", "B") for s in ("native", "rust", "managed")},
                "missing native/Rust/managed replica receipt", 3)
        for (replica, stage), (row, fact) in stages.items():
            predecessors = {"native": [], "rust": ["native"], "managed": ["native", "rust"]}[stage]
            expected = sorted([stages[(replica, s)][1] for s in predecessors], key=lambda f: ordinal(f["path"]))
            require(row["predecessorReceipts"] == expected, "wrong stage predecessor receipt", 4)
            require(all(stages[(replica, s)][0]["finishedUtc"] <= row["startedUtc"] for s in predecessors),
                    "stage ran before predecessor completion")
            if stage != "managed":
                require(not row["embeddedOutputs"], "unexpected stage embedding")
            else:
                origins = [f for s in predecessors for f in stages[(replica, s)][0]["outputs"]]
                require(sorted((e["origin"] for e in row["embeddedOutputs"]), key=lambda f: ordinal(f["path"]))
                        == sorted(origins, key=lambda f: ordinal(f["path"])),
                        "missing/extra native/Rust publication join", 4)
                for embed in row["embeddedOutputs"]:
                    require(embed["output"] in row["outputs"]
                            and embed["output"]["length"] == embed["origin"]["length"]
                            and embed["output"]["sha256"] == embed["origin"]["sha256"],
                            "managed embedded output mismatch", 4)
        for stage in ("native", "rust", "managed"):
            def normalized(replica):
                prefix = "replicas/" + replica + "/" + stage + "/"
                return [(f["path"][len(prefix):], f["length"], f["sha256"])
                        for f in stages[(replica, stage)][0]["outputs"]]
            require(normalized("A") == normalized("B"), "rebuild byte mismatch: " + stage, 6)
        comparison = self.doc(release["build"]["rebuildComparison"], "comparison")
        require(comparison["status"] == "pass" and comparison["stageReceipts"] == stage_facts
                and comparison["inputManifestSha256"] == self.m["buildInputs"]["sha256"],
                "missing/different full rebuild comparison", 6)
        self.managed_outputs = stages[("A", "managed")][0]["outputs"]
        all_outputs = {f["path"] for row, _fact in stages.values() for f in row["outputs"]}
        require({p for p in self.inventory if p.startswith("replicas/")} == all_outputs,
                "extra or reused file in stage output tree")
        a_outputs = [f for s in ("native", "rust", "managed") for f in stages[("A", s)][0]["outputs"]]
        for path in self.shipped:
            require(self.inventory[path] in a_outputs, "shipped file lacks replica A stage output", 3)

    def verify_application(self):
        release = self.m["release"]
        app = release["application"]
        pub = self.doc(app["publishedTree"], "published")
        sdk = self.file_manifest(app["sdkAssets"])
        pub_paths = paths(pub["files"])
        prefix = "replicas/A/managed/wwwroot/"
        require(bool(pub_paths) and all(p.startswith(prefix) for p in pub_paths)
                and pub_paths == paths(self.managed_outputs), "missing/extra published tree file", 8)
        require(paths(sdk) <= pub_paths and app["clientWasm"] in sdk
                and {p for p in pub_paths if p.startswith(prefix + "_framework/")} <= paths(sdk),
                "SDK assets missing from publication", 8)
        required = [app[k] for k in ("clientWasm", "serviceWorker", "cspDocument", "pwaManifest")]
        required += app["workerSources"] + list(release["analysis"][k] for k in ("loader", "wasm"))
        required += [release["renderer"][k] for k in ("loader", "wasm")]
        require(all(f in pub["files"] for f in required), "published core file missing", 8)
        require(pub_paths <= set(self.shipped), "published file lacks component/material association", 3)
        require(all(not p.endswith(("/release-identity.json", "/release-index.json"))
                    and "/evidence/" not in p for p in pub_paths), "published manifest hash-cycle exclusion violated")
        # Every shipped byte has a closed source/notice graph, not just binaries.
        require(set(self.shipped) <= paths(pub["files"]) | paths(self.file_manifest(release["compiler"]["inventory"]))
                | {release["compiler"]["zip"]["path"]}, "extra shipped material outside release boundary")
        # Provenance document is a component build association, never a wrapper version guess.
        provenance = self.doc(release["renderer"]["provenance"], "componentBuild")
        require(release["renderer"]["loader"] in provenance["outputs"]
                and release["renderer"]["wasm"] in provenance["outputs"], "renderer provenance output mismatch", 4)
        require(provenance["componentId"] in set(self.shipped.values()), "renderer provenance component missing")

    def verify_archives(self):
        limits = self.m["archiveLimits"]
        total_bytes = total_members = 0
        seen_archives = set()
        for archive in self.m["archives"]:
            self.budget.check()
            require(archive["file"]["path"] not in seen_archives, "duplicate archive")
            seen_archives.add(archive["file"]["path"])
            expected = {f["path"]: f for f in archive["members"]}
            unique_paths(expected)
            require(len(expected) <= limits["maxMembers"], "archive member budget")
            seen = set()
            require(archive["file"]["length"] <= limits["maxTotalBytes"]
                    + limits["maxMembers"] * 2048 + 65557, "archive container budget")
            with self.tree.open(archive["file"]["path"]) as stream:
                try:
                    if archive["format"] == "zip":
                        # Bound central-directory allocation BEFORE ZipFile reads it.
                        # A ZIP member has at least a 46-byte central header; compressed
                        # data stays streamed. The container itself is a declared FileFact.
                        require(archive["file"]["length"] <= limits["maxTotalBytes"]
                                + limits["maxMembers"] * 2048 + 65557, "ZIP container budget")
                        self.preflight_zip(stream, limits["maxMembers"])
                        with zipfile.ZipFile(stream) as zip_container:
                            require(len(zip_container.infolist()) <= limits["maxMembers"], "ZIP entry budget")
                            for member in zip_container.infolist():
                                require(member.orig_filename == member.filename, "ZIP NUL/control filename")
                                path_name(member.orig_filename)
                                mode = member.external_attr >> 16
                                require(not member.is_dir() and (stat.S_IFMT(mode) in (0, stat.S_IFREG))
                                        and not (member.flag_bits & 1), "ZIP link/special/encrypted member")
                                total_bytes, total_members = self.archive_member(
                                    member.filename, member.file_size, lambda m=member: zip_container.open(m),
                                    expected, seen, limits, total_bytes, total_members)
                    else:
                        total_bytes, total_members = self.verify_tar(
                            stream, expected, seen, limits, total_bytes, total_members)
                except (zipfile.BadZipFile, tarfile.TarError, RuntimeError, EOFError, OSError):
                    raise Refusal(2, "invalid archive") from None
            require(set(seen) == set(expected), "missing archive member", 3)

    def verify_tar(self, stream, expected, seen, limits, total_bytes, total_members):
        # Strict regular-file USTAR only. Inspect type and length BEFORE reading a
        # payload; tarfile's PAX/GNU extension parsing can allocate before yielding.
        while True:
            self.budget.check()
            header = stream.read(512)
            require(len(header) == 512, "truncated TAR header")
            if header == bytes(512):
                require(stream.read(512) == bytes(512), "TAR end marker missing")
                while True:
                    self.budget.check()
                    padding = stream.read(CHUNK)
                    if not padding:
                        return total_bytes, total_members
                    require(not any(padding), "TAR trailing content")
            require(header[156:157] in (b"0", b"\0") and not any(header[157:257]),
                    "TAR link/special/extended member")
            require(header[257:263] in (b"ustar\0", bytes(6)), "unsupported TAR format")
            try:
                size_text = header[124:136].rstrip(b"\0 ").lstrip(b" ")
                checksum_text = header[148:156].rstrip(b"\0 ").lstrip(b" ")
                require(re.fullmatch(b"[0-7]+", size_text) is not None
                        and re.fullmatch(b"[0-7]+", checksum_text) is not None, "invalid TAR number")
                size = int(size_text, 8)
                require(sum(header[:148]) + 8 * 32 + sum(header[156:]) == int(checksum_text, 8),
                        "TAR checksum mismatch")
                def tar_name(field):
                    head, separator, tail = field.partition(b"\0")
                    require(not separator or not any(tail), "TAR NUL/control filename")
                    return head.decode("utf-8", "strict")
                name = tar_name(header[:100])
                prefix = tar_name(header[345:500])
                if prefix:
                    name = prefix + "/" + name
            except (ValueError, UnicodeError):
                raise Refusal(2, "invalid TAR header") from None

            class Member:
                def __init__(self, source, remaining):
                    self.source, self.remaining = source, remaining

                def __enter__(self):
                    return self

                def __exit__(self, exc_type, exc_value, traceback):
                    return False

                def read(self, count):
                    data = self.source.read(min(count, self.remaining))
                    self.remaining -= len(data)
                    return data
            total_bytes, total_members = self.archive_member(
                name, size, lambda: Member(stream, size), expected, seen, limits,
                total_bytes, total_members)
            padding_size = (-size) % 512
            require(stream.read(padding_size) == bytes(padding_size), "invalid TAR padding")

    def preflight_zip(self, stream, max_members):
        stream.seek(0, os.SEEK_END)
        length = stream.tell()
        stream.seek(max(0, length - 65557))
        tail = stream.read(65557)
        offset = tail.rfind(b"PK\x05\x06")
        require(offset >= 0 and len(tail) - offset >= 22, "ZIP end record missing")
        import struct
        _, disk, start_disk, count_disk, count, size, start, comment = struct.unpack(
            "<4s4H2IH", tail[offset:offset + 22])
        require(disk == start_disk == 0 and count == count_disk and count <= max_members
                and count != 65535 and size != 0xFFFFFFFF and start != 0xFFFFFFFF
                and size <= min(METADATA_BYTES, max_members * (46 + 1024)) and start + size <= length
                and offset + 22 + comment == len(tail), "ZIP central directory budget/shape")
        stream.seek(start)
        consumed = 0
        for _ in range(count):
            self.budget.check()
            header = stream.read(46)
            require(len(header) == 46 and header[:4] == b"PK\x01\x02", "invalid ZIP central header")
            fields = struct.unpack("<4s6H3I5H2I", header)
            name_bytes, extra_bytes, comment_bytes = fields[10:13]
            require(0 < name_bytes <= 1024 and extra_bytes <= 1024 and comment_bytes <= 1024,
                    "ZIP member metadata budget")
            consumed += 46 + name_bytes + extra_bytes + comment_bytes
            require(consumed <= size, "ZIP central directory size mismatch")
            require(len(stream.read(name_bytes + extra_bytes + comment_bytes))
                    == name_bytes + extra_bytes + comment_bytes, "truncated ZIP central metadata")
        require(consumed == size, "ZIP central directory count/size mismatch")
        stream.seek(0)

    def archive_member(self, name, size, opener, expected, seen, limits, total_bytes, total_members):
        self.budget.check()
        path_name(name)
        require(name in expected, "extra archive member")
        require(name not in seen, "archive duplicate/case alias")
        seen.add(name)
        fact = expected[name]
        total_bytes += size
        total_members += 1
        require(size <= limits["maxFileBytes"] and total_bytes <= limits["maxTotalBytes"]
                and total_members <= limits["maxMembers"], "archive expansion budget")
        require(size == fact["length"], "archive member byte length mismatch", 4)
        digest, length = hashlib.sha256(), 0
        with opener() as member:
            while True:
                self.budget.check()
                block = member.read(min(CHUNK, fact["length"] - length + 1))
                if not block:
                    break
                length += len(block)
                require(length <= fact["length"], "archive member expansion overflow", 4)
                digest.update(block)
        require(length == fact["length"] and digest.hexdigest() == fact["sha256"],
                "archive member SHA-256 mismatch", 4)
        return total_bytes, total_members


def verify_inputs(inputs, out, *, cancel=None, deadline=None):
    """Validate supplied immutable bytes, returning a summary; never create out.

    `deadline` is an absolute time.monotonic() value, `cancel` is a predicate.
    All paths in public diagnostics are logical, not caller/host paths.
    """
    budget = Budget(cancel, deadline)
    budget.check()
    inputs = no_host_links(inputs)
    out = no_host_links(out)
    require(inputs != out and inputs.parent != out and inputs.parent not in out.parents,
            "output overlaps input tree")
    if out.exists():
        require(out.is_dir() and not any(out.iterdir()), "output must be absent or empty (partial output refused)")
    tree = InputTree(inputs.parent, budget)
    try:
        data = tree.read(inputs.name)
        manifest = load_json(data, budget)
        schema = Schema()
        schema.validate(manifest)
        result = Verifier(tree, schema, manifest, inputs.name).verify()
        require(tree.read(inputs.name) == data, "input manifest changed during verification", 4)
        budget.check()
        return result
    finally:
        tree.close()


def unavailable_stage(name, *_args, **_kwargs):
    raise Refusal(3, "missing implemented/accepted stage: " + name
                  + "; requires root real stage receipts/material decisions; no output written")


def build_components(*args, **kwargs):
    return unavailable_stage("native recipes (A2 deferred)", *args, **kwargs)


def verify_rebuild(*args, **kwargs):
    return unavailable_stage("external six-stage join command (A1 validates supplied receipts only)", *args, **kwargs)


def stage_materials(*args, **kwargs):
    return unavailable_stage("assembly and recipient policy approval", *args, **kwargs)


def verify_publication(*args, **kwargs):
    return unavailable_stage("post-deployment publication/header evidence", *args, **kwargs)


def write_identity(*args, **kwargs):
    return unavailable_stage("final identity assembly", *args, **kwargs)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="action", required=True)
    for name in ("verify-inputs", "rebuild", "verify-rebuild", "assemble", "verify-publication"):
        command = sub.add_parser(name)
        command.add_argument("--inputs", required=True)
        command.add_argument("--out", required=True)
        command.add_argument("--deadline-seconds", type=int, default=300)
        if name == "rebuild":
            command.add_argument("--stage", choices=["native"], required=True)
            command.add_argument("--replica", choices=["A", "B"], required=True)
    args = parser.parse_args(argv)
    cancelled = False

    def stop(_signum, _frame):
        nonlocal cancelled
        cancelled = True
    previous = {s: signal.signal(s, stop) for s in (signal.SIGINT, signal.SIGTERM)}
    try:
        require(1 <= args.deadline_seconds <= 86400, "deadline must be 1..86400 seconds")
        if args.action != "verify-inputs":
            unavailable_stage(args.action)
        result = verify_inputs(args.inputs, args.out, cancel=lambda: cancelled,
                               deadline=time.monotonic() + args.deadline_seconds)
        sys.stdout.buffer.write(canonical(result))
        return 0
    except Refusal as error:
        print("release-inputs: " + str(error), file=sys.stderr)
        return error.code
    except KeyboardInterrupt:
        return 130
    except (OSError, ValueError, RecursionError):
        print("release-inputs: invalid or unavailable input; no output written", file=sys.stderr)
        return 2
    finally:
        for sig, handler in previous.items():
            signal.signal(sig, handler)


if __name__ == "__main__":
    sys.exit(main())
