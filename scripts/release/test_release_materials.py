#!/usr/bin/env python3
"""Offline stdlib A1 tests. Synthetic byte graph; no compiler or license claims."""
import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import time
from typing import Any
import unittest
import zipfile

sys.dont_write_bytecode = True
HERE = Path(__file__).parent
spec = importlib.util.spec_from_file_location("release_materials", HERE / "build-release.py")
assert spec and spec.loader
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


def fact(path, data):
    return {"path": path, "length": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def sorted_facts(items):
    return sorted(items, key=lambda f: release.ordinal(f["path"]))


class Synthetic:
    def __init__(self, root):
        self.root = root
        self.root.mkdir(parents=True)
        self.files: dict[str, Any] = {}
        self.docs: dict[str, Any] = {}
        self.manifest: dict[str, Any] = {}
        self.make()

    def put(self, path, data):
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        result = fact(path, data)
        self.files[path] = result
        return result

    def doc(self, path, value):
        self.docs[path] = copy.deepcopy(value)
        return self.put(path, release.canonical(value))

    def fm(self, path, entries):
        return self.doc(path, {"schemaVersion": 1, "files": sorted_facts(entries)})

    def make(self):
        source = self.put("sources/синтетичний source.txt", (HERE / "fixtures/source.txt").read_bytes())
        text = self.put("materials/NOTICE.txt", (HERE / "fixtures/NOTICE.txt").read_bytes())
        member = self.put("objects/synthetic-member.txt", b"SYNTHETIC object, not executable\n")
        review_ref = self.put("materials/reviewer.txt", b"SYNTHETIC ONLY: no real release approval\n")
        readme = self.put("materials/BUILD.md", b"Synthetic graph; no real build or licensing claim.\n")
        compatibility = self.put("materials/COMPATIBILITY.md", b"Synthetic tooling only; real release blocked.\n")
        source_manifest = self.fm("meta/sources.json", [source])
        tree = self.fm("meta/tree.json", [source])
        tool = self.put("tools/synthetic-tool.txt", b"SYNTHETIC tool identity\n")
        toolchain = self.fm("meta/toolchain.json", [tool])
        recipes = self.fm("meta/recipes.json", [source])
        dependencies = self.fm("meta/dependencies.json", [text])
        patches = self.doc("meta/patches.json", {"schemaVersion": 1, "patches": []})
        link = self.doc("meta/selected-link.json", {"schemaVersion": 1, "componentId": "synthetic",
            "members": [{"path": member["path"], "member": member, "sources": [source], "workIds": ["synthetic-work"]}]})
        assoc = self.doc("meta/notice-association.json", {"schemaVersion": 1, "componentId": "synthetic",
            "noticeIds": ["synthetic-notice"], "workIds": ["synthetic-work"]})
        outputs = {}
        embedded = {}
        for replica in ("A", "B"):
            native_prefix = "replicas/" + replica + "/native/"
            native = [self.put(native_prefix + "resources/engine.js", b"// SYNTHETIC loader, never executed\n"),
                      self.put(native_prefix + "resources/engine.wasm", b"SYNTHETIC non-WASM bytes\n")]
            buffer = io.BytesIO()
            with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_DEFLATED) as archive:
                for item in native:
                    info = zipfile.ZipInfo(Path(item["path"]).name, date_time=(2020, 1, 1, 0, 0, 0))
                    info.compress_type = zipfile.ZIP_DEFLATED
                    info.external_attr = 0o100644 << 16
                    archive.writestr(info, (self.root / item["path"]).read_bytes())
            native.append(self.put(native_prefix + "compiler.zip", buffer.getvalue()))
            rust = [self.put("replicas/" + replica + "/rust/analysis.js", b"// SYNTHETIC analysis loader\n"),
                    self.put("replicas/" + replica + "/rust/analysis.wasm", b"SYNTHETIC analysis non-WASM\n")]
            pub_prefix = "replicas/" + replica + "/managed/wwwroot/"
            pub = []
            embeds = []
            for item in native + rust:
                target_name = "compiler.zip" if item["path"].endswith("compiler.zip") else Path(item["path"]).name
                target = self.put(pub_prefix + target_name, (self.root / item["path"]).read_bytes())
                pub.append(target)
                embeds.append({"output": target, "origin": item})
            for name in ("client.wasm", "service-worker.js", "index.html", "manifest.webmanifest", "worker.js"):
                pub.append(self.put(pub_prefix + name, ("SYNTHETIC " + name + "\n").encode()))
            outputs[(replica, "native")] = sorted_facts(native)
            outputs[(replica, "rust")] = sorted_facts(rust)
            outputs[(replica, "managed")] = sorted_facts(pub)
            embedded[replica] = sorted(embeds, key=lambda e: e["output"]["path"])
        self.outputs = outputs
        native = outputs[("A", "native")]
        runtime_files = [f for f in native if "/resources/" in f["path"]]
        compiler_inventory = self.fm("meta/compiler-files.json", runtime_files)
        archive = next(f for f in native if f["path"].endswith("compiler.zip"))
        archive_members = [{**f, "path": Path(f["path"]).name} for f in runtime_files]
        closure = hashlib.sha256("\n".join(f"{f['path']}\t{f['length']}\t{f['sha256']}"
                                         for f in archive_members).encode()).hexdigest()
        compiler = {"profile": 6, "inventory": compiler_inventory, "closureSha256": closure, "zip": archive,
            "components": [{"componentId": "synthetic", "role": "engine", "loader": runtime_files[0],
                "wasm": runtime_files[1], "sourceManifest": source_manifest, "patchManifest": patches,
                "selectedLinkManifest": link, "noticeAssociation": assoc}],
            "formats": [], "admittedTuples": [{"profile": 6, "phase": 6, "engineId": "pdftex",
                "tool": "latex", "workerRole": "compile", "componentId": "synthetic"}]}
        approved = self.doc("meta/approved-profile.json", compiler)
        stage_outputs = {s: [f["path"].removeprefix("replicas/A/" + s + "/") for f in outputs[("A", s)]]
                         for s in ("native", "rust", "managed")}
        lock = self.doc("meta/build-inputs.json", {"schemaVersion": 1, "sourceTreeSha256": tree["sha256"],
            "recipeManifest": recipes, "toolchainManifest": toolchain, "dependencyManifest": dependencies,
            "sourceDateEpoch": 1577836800, "configuration": release.CONFIGURATION,
            "approvedProfileSha256": approved["sha256"], "stageOutputs": stage_outputs})
        shipped = sorted_facts(outputs[("A", "native")] + outputs[("A", "managed")])
        pub_files = outputs[("A", "managed")]
        pub = self.doc("meta/published.json", {"schemaVersion": 1, "files": pub_files,
            "exclusions": ["evidence/", "release-identity.json", "release-index.json"]})
        sdk = self.fm("meta/sdk-assets.json", outputs[("A", "managed")])
        build_receipt = self.doc("meta/component-build.json", {"schemaVersion": 1,
            "componentId": "synthetic", "sourceManifest": source_manifest, "patchManifest": patches,
            "selectedLinkManifest": link, "inputManifestSha256": lock["sha256"], "outputs": shipped})
        decision = self.doc("materials/decision.json", {"schemaVersion": 1, "status": "approved",
            "policy": "SYNTHETIC FIXTURE ONLY. Not real license clearance.", "reviewReference": review_ref,
            "components": ["synthetic"]})
        inventory = self.doc("materials/component-inventory.json", {"schemaVersion": 1, "components": [{
            "componentId": "synthetic", "role": "runtime", "shippedFiles": shipped,
            "sourceFilesManifest": source_manifest, "upstreamIdentity": "authored-synthetic-test-v1",
            "generatedFrom": [], "patches": [], "buildReceipt": build_receipt, "selectedLinkManifest": link,
            "licenseEvidence": [text], "licenseExpression": "LicenseRef-Synthetic-Test-Only",
            "provenanceStatus": "complete", "principalTextStatus": "present", "distributionStatus": "approved",
            "reviewDecision": decision}]})
        notices = self.doc("materials/notices-index.json", {"schemaVersion": 1, "notices": [{
            "id": "synthetic-notice", "componentId": "synthetic", "text": text, "source": source,
            "member": member, "workId": "synthetic-work", "kind": "complete"}], "works": [{
            "workId": "synthetic-work", "componentIds": ["synthetic"], "sourceFiles": [source],
            "noticeIds": ["synthetic-notice"], "generatedFrom": []}]})
        source_access = self.doc("materials/source-access.json", {"schemaVersion": 1, "components": [{
            "componentId": "synthetic", "route": "accompanying", "files": [source], "decision": decision}]})
        preservation = self.doc("meta/source-preservation.json", {"schemaVersion": 1,
            "beforeSha256": tree["sha256"], "afterSha256": tree["sha256"]})
        stages = {}
        for replica in ("A", "B"):
            for index, stage in enumerate(("native", "rust", "managed")):
                predecessor_names = {"native": [], "rust": ["native"], "managed": ["native", "rust"]}[stage]
                predecessors = sorted_facts([stages[(replica, p)] for p in predecessor_names])
                stages[(replica, stage)] = self.doc("receipts/" + replica + "-" + stage + ".json", {
                    "schemaVersion": 1, "id": "R4-BUILD-" + stage + "-" + replica,
                    "status": "pass", "command": "SYNTHETIC fixture only (not executed)",
                    "receipt": "synthetic/" + replica + "-" + stage + ".json", "stage": stage, "replica": replica,
                    "sourceTreeSha256": tree["sha256"], "inputManifestSha256": lock["sha256"],
                    "toolchainManifest": toolchain, "predecessorReceipts": predecessors,
                    "outputs": outputs[(replica, stage)],
                    "embeddedOutputs": embedded[replica] if stage == "managed" else [],
                    "exitCode": 0, "sourcePreservation": preservation,
                    "freshBuild": {"runId": replica + "-" + stage, "outputRootId": replica + "-" + stage,
                                   "initialOutputFiles": []},
                    "startedUtc": "2020-01-01T00:00:0" + str(index * 2) + "Z",
                    "finishedUtc": "2020-01-01T00:00:0" + str(index * 2 + 1) + "Z"})
        comparison = self.doc("meta/rebuild-comparison.json", {"schemaVersion": 1, "id": "R4-REP-01",
            "status": "pass", "command": "SYNTHETIC comparison, no builds", "receipt": "synthetic/compare.json",
            "stageReceipts": sorted_facts(stages.values()), "inputManifestSha256": lock["sha256"]})
        pub_map = {Path(f["path"]).name: f for f in outputs[("A", "managed")]}
        headers = self.put("materials/headers.txt", b"SYNTHETIC headers, not deployed\n")
        rules = self.put("meta/rules.txt", b"SYNTHETIC rule metadata\n")
        managed_lock = self.put("meta/managed-lock.txt", b"SYNTHETIC managed dependencies\n")
        self.manifest = {"schemaVersion": 1, "release": {"schemaVersion": 1,
            "source": {"baseCommit": "b4ec43d1cac535408d44ac3d28a583f1c08b509f", "treeManifest": tree,
                       "treeSha256": tree["sha256"]},
            "build": {"recipeManifest": recipes, "toolchainManifest": toolchain, "dependencyManifest": dependencies,
                "sourceDateEpoch": 1577836800, "stageReceipts": sorted_facts(stages.values()),
                "rebuildComparison": comparison, "configuration": release.CONFIGURATION},
            "compiler": compiler,
            "renderer": {"loader": pub_map["engine.js"], "wasm": pub_map["engine.wasm"], "provenance": build_receipt},
            "analysis": {"loader": pub_map["analysis.js"], "wasm": pub_map["analysis.wasm"], "ruleMetadata": [rules]},
            "application": {"publishedTree": pub, "clientWasm": pub_map["client.wasm"], "sdkAssets": sdk,
                "sdkManifestVersion": "synthetic-v1", "serviceWorker": pub_map["service-worker.js"],
                "workerSources": [pub_map["worker.js"]], "cspDocument": pub_map["index.html"], "cspHeaders": headers,
                "pwaManifest": pub_map["manifest.webmanifest"], "managedDependencyLock": managed_lock},
            "materials": {"componentInventory": inventory, "noticesIndex": notices, "sourceAccess": source_access,
                "rebuildInstructions": readme, "licenseDecision": decision, "compatibilityMatrix": compatibility}},
            "buildInputs": lock, "approvedProfileIdentity": approved,
            "archiveLimits": {"maxFileBytes": 4096, "maxTotalBytes": 65536, "maxMembers": 32},
            "archives": [{"file": archive, "format": "zip", "members": archive_members}], "files": []}
        self.save()

    def save(self):
        self.manifest["files"] = sorted_facts(self.files.values())
        (self.root / "manifest.json").write_bytes(release.canonical(self.manifest))

    def edit(self, path, change):
        """Rehash enclosing facts after a semantic mutation, not a byte-tamper test.

        Digest-only bindings intentionally stay frozen; wrong stage/source input
        tests must fail their semantic join even with correct enclosing FileFacts.
        """
        old = self.files[path]
        value = self.docs[path]
        change(value)
        new = self.doc(path, value)
        updates = [(old, new)]
        for _ in range(len(self.docs) + 2):
            changed = False

            def replace(value: Any) -> Any:
                nonlocal changed
                if isinstance(value, dict):
                    for before, after in updates:
                        if value == before:
                            changed = True
                            return copy.deepcopy(after)
                    return {k: replace(v) for k, v in value.items()}
                if isinstance(value, list):
                    return [replace(v) for v in value]
                return value
            for name, doc in list(self.docs.items()):
                changed = False
                revised = replace(doc)
                if changed:
                    before = self.files[name]
                    after = self.doc(name, revised)
                    updates.append((before, after))
            self.manifest = replace(self.manifest)
            # Repeat only while new hashes still need propagation.
            if not any(any(f == old for f in release.facts(doc)) for old, _new in updates
                       for doc in self.docs.values()):
                break
        self.save()

    def rebind_build_hashes(self):
        self.edit("meta/build-inputs.json", lambda d: d.update(
            approvedProfileSha256=self.files["meta/approved-profile.json"]["sha256"]))
        lock_sha = self.files["meta/build-inputs.json"]["sha256"]
        for path in ["meta/component-build.json", "meta/rebuild-comparison.json"] + [
                "receipts/" + replica + "-" + stage + ".json"
                for replica in ("A", "B") for stage in ("native", "rust", "managed")]:
            self.edit(path, lambda d: d.update(inputManifestSha256=lock_sha))

    def approve_compiler(self, change):
        """Pin a changed synthetic compiler in the separate approval/lock DAG."""
        self.edit("meta/approved-profile.json", change)
        self.manifest["release"]["compiler"] = copy.deepcopy(self.docs["meta/approved-profile.json"])
        self.rebind_build_hashes()

    def biber_callers(self, callers):
        # The fixture's synthetic executable now plays ONLY the bibliography
        # component role. This is metadata validation, not a real Biber binary.
        def change(compiler):
            compiler["components"][0]["role"] = "bibliography"
            compiler["admittedTuples"] = [{"profile": compiler["profile"], "phase": 10,
                "engineId": caller, "tool": "biber", "workerRole": "biber", "componentId": "synthetic"}
                for caller in sorted(callers, key=release.ordinal)]
        self.approve_compiler(change)

    def snapshot(self):
        return {str(p.relative_to(self.root)): hashlib.sha256(p.read_bytes()).hexdigest()
                for p in self.root.rglob("*") if p.is_file() and not p.is_symlink()}


class MaterialTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="r4-a1-")
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.case = Synthetic(self.base / "inputs")
        self.out = self.base / "out"

    def verify(self, **kwargs):
        return release.verify_inputs(self.case.root / "manifest.json", self.out, **kwargs)

    def refused(self, code, message=None, **kwargs):
        before = self.case.snapshot()
        with self.assertRaises(release.Refusal) as caught:
            self.verify(**kwargs)
        self.assertEqual(caught.exception.code, code, str(caught.exception))
        if message:
            self.assertIn(message, str(caught.exception))
        self.assertFalse(self.out.exists())
        self.assertEqual(self.case.snapshot(), before, "verification mutated input bytes")

    def test_complete_synthetic_graph_and_read_only_cli(self):
        before = self.case.snapshot()
        self.assertEqual(self.verify()["status"], "pass")
        command = [sys.executable, str(HERE / "build-release.py"), "verify-inputs", "--inputs",
                   str(self.case.root / "manifest.json"), "--out", str(self.out)]
        run = subprocess.run(command, capture_output=True, check=False)
        self.assertEqual(run.returncode, 0, run.stderr.decode())
        self.assertEqual(json.loads(run.stdout)["scope"], "input-integrity-only")
        self.assertFalse(self.out.exists())
        self.assertEqual(before, self.case.snapshot())

    def test_missing_notice(self):
        (self.case.root / "materials/NOTICE.txt").unlink()
        self.refused(3)

    def test_tampered_notice(self):
        (self.case.root / "materials/NOTICE.txt").write_bytes(b"X" * self.case.files["materials/NOTICE.txt"]["length"])
        self.refused(4, "SHA-256")

    def test_tampered_source_length(self):
        (self.case.root / "sources/синтетичний source.txt").write_bytes(b"changed")
        self.refused(4, "length")

    def test_extra_executable(self):
        self.case.put("replicas/A/native/old-object.wasm", b"unassociated old output")
        self.case.save()
        self.refused(2, "extra or reused")

    def test_extra_uninventoried_material(self):
        (self.case.root / "materials/extra.txt").write_text("unlisted")
        self.refused(2, "extra")

    def test_extra_inventoried_material_without_graph_edge(self):
        self.case.put("materials/extra.txt", b"listed but not associated")
        self.case.save()
        self.refused(2, "unassociated")

    def test_source_and_output_symlinks(self):
        path = self.case.root / "materials/NOTICE.txt"
        original = self.base / "outside.txt"
        path.rename(original)
        path.symlink_to(original)
        self.refused(2, "symlink")
        path.unlink()
        original.rename(path)
        self.out.symlink_to(self.base / "absent", target_is_directory=True)
        with self.assertRaises(release.Refusal) as caught:
            self.verify()
        self.assertEqual(caught.exception.code, 2)
        self.assertTrue(self.out.is_symlink())

    def test_parent_symlink_and_hardlink(self):
        alias = self.base / "alias"
        alias.symlink_to(self.case.root, target_is_directory=True)
        with self.assertRaises(release.Refusal):
            release.verify_inputs(alias / "manifest.json", self.out)
        notice = self.case.root / "materials/NOTICE.txt"
        os.link(notice, self.base / "hardlink.txt")
        self.refused(2, "hardlinked")

    def test_paths_reject_absolute_traversal_controls_aliases(self):
        for path in ("/etc/passwd", "../escape", "a//b", "a/./b", "a/../b", "C:bad", "a\\b", "a\nb",
                     "e\u0301.txt", "x" * 1025, "a/", "\ud800"):
            with self.subTest(path=repr(path)), self.assertRaises(release.Refusal):
                release.path_name(path)
        for names in (("A", "a"), ("é", "e\u0301"), ("x", "x")):
            with self.assertRaises(release.Refusal):
                release.unique_paths(names)
        release.path_name("розділ/тест 🧪.tex")
        release.path_name("я" * 512)

    def test_manifest_alias_and_unsorted_rows(self):
        first = self.case.manifest["files"][0]
        self.case.manifest["files"].append({**first, "path": first["path"].upper()})
        (self.case.root / "manifest.json").write_bytes(release.canonical(self.case.manifest))
        self.refused(2)

    def test_duplicate_json_fields_and_noncanonical(self):
        manifest = self.case.root / "manifest.json"
        manifest.write_bytes((HERE / "fixtures/invalid-json.json").read_bytes())
        self.refused(2, "duplicate JSON")
        self.case.save()
        manifest.write_text(json.dumps(self.case.manifest, indent=2))
        self.refused(2, "canonical")

    def test_numbers_versions_and_unknown_fields(self):
        for data in (b'{"x":NaN}\n', b'{"x":Infinity}\n', b'{"x":1e400}\n',
                     b'{"x":9007199254740992}\n', b'{"x":1.0}\n'):
            with self.assertRaises(release.Refusal):
                release.load_json(data, release.Budget())
        schema = release.Schema()
        for value in (True, -1, 2, "1"):
            altered = copy.deepcopy(self.case.manifest)
            altered["schemaVersion"] = value
            with self.assertRaises(release.Refusal):
                schema.validate(altered)
        altered = copy.deepcopy(self.case.manifest)
        altered["execute"] = "arbitrary command forbidden"
        with self.assertRaises(release.Refusal):
            schema.validate(altered)

    def test_metadata_size_rows_and_depth(self):
        with self.assertRaises(release.Refusal):
            release.load_json(b" " * (release.METADATA_BYTES + 1), release.Budget())
        with self.assertRaises(release.Refusal):
            release.load_json(release.canonical([0] * (release.MAX_ROWS + 1)), release.Budget())
        with self.assertRaises(release.Refusal):
            release.load_json(b"[" * 65 + b"0" + b"]" * 65 + b"\n", release.Budget())

    def test_unknown_provenance_is_pending_even_when_hashes_match(self):
        self.case.edit("materials/component-inventory.json", lambda d: d["components"][0].update(
            provenanceStatus="unknown", upstreamIdentity=None))
        self.refused(7, "unknown provenance")

    def test_unknown_license_and_distribution_is_pending(self):
        self.case.edit("materials/component-inventory.json", lambda d: d["components"][0].update(
            licenseExpression=None, distributionStatus="pending", reviewDecision=None))
        self.refused(7, "unreviewed license")

    def test_unknown_license_sentinel_not_approved(self):
        self.case.edit("materials/component-inventory.json", lambda d: d["components"][0].update(
            licenseExpression="NOASSERTION"))
        self.refused(7, "unreviewed license")

    def test_approved_flag_alone_does_not_select_noncommercial_license(self):
        self.case.edit("materials/decision.json", lambda d: d.update(status="approved", policy="noncommercial"))
        self.refused(7, "project policy")

    def test_blocked_policy_and_noncommercial_intent_not_approval(self):
        self.case.edit("materials/decision.json", lambda d: d.update(status="blocked", policy="noncommercial"))
        self.refused(7, "project policy")

    def test_excerpt_not_complete_principal_text(self):
        self.case.edit("materials/notices-index.json", lambda d: d["notices"][0].update(kind="excerpt"))
        self.refused(7, "complete principal")

    def test_wrong_work_member_and_source_associations(self):
        for field, value, message in (("workId", "missing-work", "Work association"),
                                      ("source", self.case.files["materials/NOTICE.txt"], "source/Work"),
                                      ("member", self.case.files["materials/NOTICE.txt"], "selected-member")):
            with self.subTest(field=field):
                root = self.base / field
                original = self.case
                self.case = Synthetic(root)
                self.case.edit("materials/notices-index.json", lambda d: d["notices"][0].update({field: value}))
                self.refused(2, message)
                self.case = original

    def test_missing_source_work(self):
        self.case.edit("materials/notices-index.json", lambda d: d["works"][0].update(sourceFiles=[]))
        self.refused(2, "Work")

    def test_selected_link_source_and_component(self):
        self.case.edit("meta/selected-link.json", lambda d: d.update(componentId="wrong"))
        self.case.rebind_build_hashes()
        self.refused(2, "selected-link component")

    def test_missing_recipient_sources(self):
        self.case.edit("materials/source-access.json", lambda d: d["components"][0].update(files=[]))
        self.refused(3, "recipient source")

    def test_mixed_profile(self):
        self.case.manifest["release"]["compiler"]["profile"] = 999
        self.case.save()
        self.refused(4, "unaccepted profile")

    def test_explicit_accepted_profile_alias_is_not_hardcoded_to_six(self):
        def update(compiler):
            compiler["profile"] = 42  # SYNTHETIC descriptive identity, not runtime admission.
            compiler["admittedTuples"][0].update(profile=42, phase=11)
        self.case.edit("meta/approved-profile.json", update)
        self.case.manifest["release"]["compiler"] = copy.deepcopy(self.case.docs["meta/approved-profile.json"])
        self.case.rebind_build_hashes()
        self.assertEqual(self.verify()["scope"], "input-integrity-only")
        self.assertFalse(self.out.exists())

    def test_wrong_role_phase_and_tool_even_if_approved_document_matches(self):
        def change(doc):
            doc["admittedTuples"][0]["phase"] = 10
        self.case.edit("meta/approved-profile.json", change)
        # Keep exact approval and build-lock binding to reach semantic tuple check.
        self.case.manifest["release"]["compiler"] = self.case.docs["meta/approved-profile.json"]
        self.case.edit("meta/build-inputs.json", lambda d: d.update(
            approvedProfileSha256=self.case.files["meta/approved-profile.json"]["sha256"]))
        self.case.save()
        self.refused(2, "unsupported Biber tool/role/component")

    def test_biber_phase10_retains_each_of_four_caller_engines(self):
        for caller in ("pdftex", "latex-dvi", "xelatex", "lualatex"):
            with self.subTest(caller=caller):
                self.case = Synthetic(self.base / caller)
                self.case.biber_callers([caller])
                before = self.case.snapshot()
                self.assertEqual(self.verify()["scope"], "input-integrity-only")
                self.assertEqual(self.case.snapshot(), before)
                self.assertFalse(self.out.exists())

    def test_biber_all_four_callers_share_approved_bibliography_closure(self):
        self.case.biber_callers(["pdftex", "latex-dvi", "xelatex", "lualatex"])
        self.assertEqual(self.verify()["status"], "pass")
        tuples = self.case.manifest["release"]["compiler"]["admittedTuples"]
        self.assertEqual({t["engineId"] for t in tuples}, {"pdftex", "latex-dvi", "xelatex", "lualatex"})
        self.assertTrue(all(t["phase"] == 10 and t["tool"] == t["workerRole"] == "biber" for t in tuples))
        self.assertFalse(self.out.exists())

    def test_biber_invented_fifth_engine_rejected_even_in_approved_closure(self):
        self.case.biber_callers(["biber"])
        self.refused(2, "unknown enum")

    def test_biber_relabelled_tool_role_or_wrong_phase_rejected(self):
        mutations = [
            {"tool": "bibtex", "workerRole": "compile"},
            {"tool": "biber", "workerRole": "compile"},
            {"tool": "latex", "workerRole": "biber"},
            {"phase": 6}, {"phase": 11}, {"phase": 7}, {"phase": 14}]
        for index, mutation in enumerate(mutations):
            with self.subTest(mutation=mutation):
                self.case = Synthetic(self.base / ("relabel-" + str(index)))
                self.case.biber_callers(["pdftex"])
                self.case.approve_compiler(lambda c: c["admittedTuples"][0].update(mutation))
                self.refused(2, "unsupported")

    def test_biber_requires_bibliography_component_role(self):
        for role in ("engine", "converter"):
            with self.subTest(role=role):
                self.case = Synthetic(self.base / ("wrong-role-" + role))
                self.case.biber_callers(["xelatex"])
                self.case.approve_compiler(lambda c: c["components"][0].update(role=role))
                self.refused(2, "unsupported Biber tool/role/component")

    def test_bibliography_component_cannot_be_relabelled_as_legacy_bibtex(self):
        self.case.biber_callers(["pdftex"])
        self.case.approve_compiler(lambda c: c["admittedTuples"][0].update(
            phase=6, tool="bibtex", workerRole="compile"))
        self.refused(2, "bibliography component cannot be relabelled")

    def test_biber_requires_exact_independently_approved_tuple_and_component(self):
        self.case.biber_callers(["pdftex"])
        self.case.manifest["release"]["compiler"]["admittedTuples"][0]["engineId"] = "xelatex"
        self.case.save()
        self.refused(4, "unaccepted profile/component/phase tuple")

    def test_biber_undeclared_bibliography_component_rejected(self):
        self.case.biber_callers(["pdftex"])
        self.case.approve_compiler(lambda c: c["admittedTuples"][0].update(componentId="missing-biber"))
        self.refused(2, "tuple profile/component mismatch")

    def test_missing_biber_tuple_cannot_match_approved_closure(self):
        self.case.biber_callers(["pdftex", "xelatex"])
        self.case.manifest["release"]["compiler"]["admittedTuples"].pop()
        self.case.save()
        self.refused(4, "unaccepted profile/component/phase tuple")

    def test_legacy_and_map_engine_phase_pairs_unchanged(self):
        for caller, phases, role in (("pdftex", (6, 11), "compile"), ("latex-dvi", (7, 12), "compile"),
                                     ("xelatex", (8, 13), "xetex"), ("lualatex", (9, 14), "luatex")):
            for phase in phases:
                with self.subTest(caller=caller, phase=phase):
                    self.case = Synthetic(self.base / (caller + str(phase)))
                    self.case.approve_compiler(lambda c: c["admittedTuples"][0].update(
                        engineId=caller, phase=phase, tool="latex", workerRole=role))
                    self.assertEqual(self.verify()["status"], "pass")

    def test_wrong_caller_engine_phase_pairs_rejected(self):
        for caller, phase, role in (("pdftex", 7, "compile"), ("latex-dvi", 11, "compile"),
                                     ("xelatex", 14, "xetex"), ("lualatex", 8, "luatex")):
            with self.subTest(caller=caller, phase=phase):
                self.case = Synthetic(self.base / ("mismatched-" + caller))
                self.case.approve_compiler(lambda c: c["admittedTuples"][0].update(
                    engineId=caller, phase=phase, tool="latex", workerRole=role))
                self.refused(2, "unsupported engine role/phase")

    def test_missing_replica_stage_receipt(self):
        self.case.manifest["release"]["build"]["stageReceipts"].pop()
        self.case.save()
        self.refused(3, "missing native/Rust/managed")

    def test_mixed_stage_source(self):
        self.case.edit("receipts/B-rust.json", lambda d: d.update(sourceTreeSha256="0" * 64))
        self.refused(4, "mixed/stale")

    def test_mixed_stage_input_lock(self):
        self.case.edit("receipts/A-native.json", lambda d: d.update(inputManifestSha256="0" * 64))
        self.refused(4, "mixed/stale")

    def test_stale_recipe_lock(self):
        self.case.manifest["release"]["build"]["sourceDateEpoch"] += 1
        self.case.save()
        self.refused(4, "stale build lock")

    def test_failed_stage_not_promoted(self):
        self.case.edit("receipts/A-native.json", lambda d: d.update(status="fail", exitCode=1))
        self.refused(5, "did not pass")

    def test_skip_status_not_accepted(self):
        self.case.edit("receipts/A-native.json", lambda d: d.update(status="skip"))
        self.refused(2, "unknown enum")

    def test_reused_object_output_refused(self):
        self.case.edit("receipts/B-native.json", lambda d: d["freshBuild"].update(initialOutputFiles=["old.o"]))
        self.refused(2, "array size")

    def test_reused_output_root_refused(self):
        self.case.edit("receipts/B-native.json", lambda d: d["freshBuild"].update(outputRootId="A-native"))
        self.refused(2, "reused stage")

    def test_wrong_predecessor(self):
        self.case.edit("receipts/B-rust.json", lambda d: d.update(
            predecessorReceipts=[self.case.files["receipts/A-native.json"]]))
        self.refused(4, "predecessor")

    def test_missing_embedded_native_rust_join(self):
        self.case.edit("receipts/A-managed.json", lambda d: d["embeddedOutputs"].pop())
        self.refused(4, "publication join")

    def test_source_mutation_receipt_refused(self):
        self.case.edit("meta/source-preservation.json", lambda d: d.update(afterSha256="0" * 64))
        self.refused(4, "source changed")

    def test_stage_cannot_reference_final_identity_hash_cycle(self):
        self.case.edit("receipts/A-native.json", lambda d: d.update(releaseIdentitySha256="0" * 64))
        self.refused(2, "cannot hash final identity")

    def test_wrong_comparison_receipt(self):
        self.case.edit("meta/rebuild-comparison.json", lambda d: d.update(status="not-run"))
        self.refused(6, "comparison")

    def test_rebuild_mismatch_genuine_different_bytes(self):
        target = "replicas/B/managed/wwwroot/client.wasm"
        old = self.case.files[target]
        new = self.case.put(target, b"DIFFERENT SYNTHETIC replica B client bytes\n")
        self.case.edit("receipts/B-managed.json", lambda d: d.update(
            outputs=sorted_facts([new if f == old else f for f in d["outputs"]])))
        self.case.save()
        self.refused(6, "rebuild byte mismatch")

    def test_published_missing_core_and_wrong_exclusions(self):
        self.case.edit("meta/published.json", lambda d: d.update(files=[
            f for f in d["files"] if not f["path"].endswith("client.wasm")]))
        self.refused(8, "published tree")

    def test_ordinal_unicode_canonicalization(self):
        bmp, supplementary = "\ue000", "🧪"
        data = release.canonical({bmp: 1, supplementary: 2})
        self.assertLess(data.find(supplementary.encode()), data.find(bmp.encode()))
        schema = release.Schema()
        ordered = [fact(supplementary, b"a"), fact(bmp, b"b")]
        schema.validate(ordered, "files")
        with self.assertRaises(release.Refusal):
            schema.validate(list(reversed(ordered)), "files")
        for names in (("A/x", "a/y"), ("a", "a/b"), ("a/b", "a")):
            with self.assertRaises(release.Refusal):
                release.unique_paths(names)

    def test_empty_directory_case_alias(self):
        (self.case.root / "Empty").mkdir()
        (self.case.root / "empty").mkdir()
        self.refused(2, "case alias")

    def test_mixed_toolchain_receipt(self):
        self.case.edit("receipts/B-native.json", lambda d: d.update(
            toolchainManifest=self.case.files["meta/dependencies.json"]))
        self.refused(4, "mixed/stale stage")

    def test_required_skipped_stage_and_reused_evidence_cannot_pass(self):
        self.case.edit("receipts/A-native.json", lambda d: d.update(
            skipCount=1, skips=[{"reason": "missing tool", "coverage": "required native output"}]))
        self.refused(5, "skipped cases")

    def test_missing_format_engine_component(self):
        compiler = self.case.manifest["release"]["compiler"]
        compiler["formats"] = [{"componentId": "synthetic-format", "engineComponentId": "missing-engine",
            "output": compiler["components"][0]["wasm"],
            "inputManifest": self.case.files["meta/sources.json"],
            "recipe": self.case.files["materials/BUILD.md"],
            "generationEnvironment": self.case.files["meta/toolchain.json"]}]
        self.case.edit("meta/approved-profile.json", lambda d: d.update(formats=compiler["formats"]))
        self.case.rebind_build_hashes()
        self.refused(3, "format/engine component")

    def test_cancellation_mid_stream_hashing(self):
        blob = self.case.put("hash-target.txt", b"a" * (1024 * 1024))
        calls = 0

        def cancel():
            nonlocal calls
            calls += 1
            return calls > 5
        tree = release.InputTree(self.case.root, release.Budget(cancel=cancel))
        try:
            with self.assertRaises(release.Refusal) as caught:
                tree.hash(blob)
            self.assertEqual(caught.exception.code, 130)
            self.assertEqual(calls, 6)
        finally:
            tree.close()
        self.assertFalse(self.out.exists())

    def test_filefact_hash_cycle(self):
        budget = release.Budget()
        with self.assertRaises(release.Refusal) as caught:
            release.acyclic({"A": {"B"}, "B": {"A"}}, "FileFact", budget)
        self.assertIn("cycle", str(caught.exception))
        # Long valid DAG checks are iterative, not recursion-limited.
        release.acyclic({str(n): {str(n + 1)} for n in range(2000)}, "FileFact", budget)
        self.case.manifest["files"].append(fact("manifest.json", b"not a valid self hash"))
        self.case.manifest["files"] = sorted_facts(self.case.manifest["files"])
        (self.case.root / "manifest.json").write_bytes(release.canonical(self.case.manifest))
        self.refused(2, "inventory itself")

    def test_component_and_work_cycles(self):
        self.case.edit("materials/component-inventory.json", lambda d: d["components"][0].update(
            generatedFrom=["synthetic"]))
        self.refused(2, "component cycle")

    def test_work_cycle(self):
        self.case.edit("materials/notices-index.json", lambda d: d["works"][0].update(
            generatedFrom=["synthetic-work"]))
        self.refused(2, "Work cycle")

    def test_archive_budgets(self):
        self.case.manifest["archiveLimits"]["maxFileBytes"] = 1
        self.case.save()
        self.refused(2, "expansion budget")

    def test_archive_member_tamper(self):
        self.case.manifest["archives"][0]["members"][0]["sha256"] = "0" * 64
        self.case.save()
        self.refused(4, "archive/member association")

    def archive_verifier(self, data, fmt, members, limits=None):
        # Focused hostile container parser tests need no otherwise valid release.
        archive_fact = self.case.put("archives/hostile." + fmt, data)
        self.case.save()
        budget = release.Budget()
        tree = release.InputTree(self.case.root, budget)
        self.addCleanup(tree.close)
        manifest = {"files": list(self.case.files.values()), "archives": [
            {"file": archive_fact, "format": fmt, "members": sorted_facts(members)}],
            "archiveLimits": limits or {"maxFileBytes": 128, "maxTotalBytes": 1024, "maxMembers": 8}}
        return release.Verifier(tree, release.Schema(), manifest, "manifest.json")

    def test_zip_actual_member_hash_and_missing_member(self):
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w") as archive:
            archive.writestr("test.txt", b"actual bytes")
        verifier = self.archive_verifier(buffer.getvalue(), "zip", [fact("test.txt", b"wrong bytes!")])
        with self.assertRaises(release.Refusal) as caught:
            verifier.verify_archives()
        self.assertEqual(caught.exception.code, 4)
        self.assertIn("SHA-256", str(caught.exception))

    def test_zip_link_traversal_duplicate_and_expansion(self):
        for name, mode, count in (("link", 0o120777, 1), ("../escape", 0o100644, 1),
                                  ("test.txt", 0o100644, 2)):
            with self.subTest(name=name):
                buffer = io.BytesIO()
                with zipfile.ZipFile(buffer, "w") as archive:
                    for _ in range(count):
                        info = zipfile.ZipInfo(name)
                        info.external_attr = mode << 16
                        archive.writestr(info, b"x")
                verifier = self.archive_verifier(buffer.getvalue(), "zip", [fact("test.txt", b"x")])
                with self.assertRaises(release.Refusal):
                    verifier.verify_archives()
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            archive.writestr("test.txt", b"x" * 65536)
        verifier = self.archive_verifier(buffer.getvalue(), "zip", [fact("test.txt", b"x" * 65536)])
        with self.assertRaises(release.Refusal) as caught:
            verifier.verify_archives()
        self.assertIn("budget", str(caught.exception))

    def test_zip_nul_filename_is_not_truncated_into_a_valid_path(self):
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w") as archive:
            archive.writestr("test.txt", b"x")
        data = buffer.getvalue().replace(b"test.txt", b"test\0txt")
        verifier = self.archive_verifier(data, "zip", [fact("test", b"x")])
        with self.assertRaises(release.Refusal) as caught:
            verifier.verify_archives()
        self.assertIn("NUL/control", str(caught.exception))

    def test_zip_central_count_lie_rejected_before_allocation(self):
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w") as archive:
            archive.writestr("test.txt", b"x")
        data = bytearray(buffer.getvalue())
        end = data.rfind(b"PK\x05\x06")
        data[end + 8:end + 12] = bytes(4)  # Claim zero members but retain central bytes.
        verifier = self.archive_verifier(bytes(data), "zip", [fact("test.txt", b"x")])
        with self.assertRaises(release.Refusal) as caught:
            verifier.verify_archives()
        self.assertIn("count/size", str(caught.exception))

    def test_zip_missing_and_extra_member(self):
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w") as archive:
            archive.writestr("test.txt", b"x")
        verifier = self.archive_verifier(buffer.getvalue(), "zip", [fact("test.txt", b"x"), fact("z.txt", b"z")])
        with self.assertRaises(release.Refusal) as caught:
            verifier.verify_archives()
        self.assertEqual(caught.exception.code, 3)
        verifier = self.archive_verifier(buffer.getvalue(), "zip", [])
        with self.assertRaises(release.Refusal) as caught:
            verifier.verify_archives()
        self.assertIn("extra", str(caught.exception))

    def test_tar_stream_positive_links_and_pax_refusal(self):
        for kind in (tarfile.REGTYPE, tarfile.SYMTYPE, tarfile.LNKTYPE, tarfile.XHDTYPE):
            with self.subTest(kind=kind):
                buffer = io.BytesIO()
                with tarfile.open(fileobj=buffer, mode="w", format=tarfile.USTAR_FORMAT) as archive:
                    info = tarfile.TarInfo("test.txt")
                    info.type = kind
                    info.size = 1 if kind in (tarfile.REGTYPE, tarfile.XHDTYPE) else 0
                    if kind in (tarfile.SYMTYPE, tarfile.LNKTYPE):
                        info.linkname = "../../host"
                    archive.addfile(info, io.BytesIO(b"x"))
                verifier = self.archive_verifier(buffer.getvalue(), "tar", [fact("test.txt", b"x")])
                if kind == tarfile.REGTYPE:
                    verifier.verify_archives()
                else:
                    with self.assertRaises(release.Refusal):
                        verifier.verify_archives()

    def test_cancellation_deadline_and_partial_output_refusal(self):
        self.refused(130, "cancelled", cancel=lambda: True)
        self.refused(124, "deadline", deadline=time.monotonic() - 1)
        calls = 0

        def cancel_after_progress():
            nonlocal calls
            calls += 1
            return calls > 300
        self.refused(130, "cancelled", cancel=cancel_after_progress)
        self.out.mkdir()
        (self.out / "partial.txt").write_bytes(b"preserve partial")
        with self.assertRaises(release.Refusal) as caught:
            self.verify()
        self.assertEqual(caught.exception.code, 2)
        self.assertEqual((self.out / "partial.txt").read_bytes(), b"preserve partial")
        self.assertEqual(list(self.out.iterdir()), [self.out / "partial.txt"])

    def test_help_and_deferred_commands_cannot_succeed(self):
        script = str(HERE / "build-release.py")
        self.assertEqual(subprocess.run([sys.executable, script, "--help"], capture_output=True).returncode, 0)
        for command in ("rebuild", "verify-rebuild", "assemble", "verify-publication"):
            args = [sys.executable, script, command, "--inputs", str(self.case.root / "manifest.json"),
                    "--out", str(self.out)]
            if command == "rebuild":
                args += ["--stage", "native", "--replica", "A"]
            result = subprocess.run(args, capture_output=True)
            self.assertEqual(result.returncode, 3)
            self.assertIn(b"missing implemented/accepted stage", result.stderr)
            self.assertFalse(self.out.exists())


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--fixture-out":
        destination = Path(sys.argv[2])
        if destination.exists():
            raise SystemExit("fixture destination must not exist")
        fixture = Synthetic(destination)
        print(json.dumps({"manifest": str(destination / "manifest.json"), "synthetic": True,
                          "files": len(fixture.files)}, sort_keys=True))
    else:
        unittest.main(verbosity=2)
