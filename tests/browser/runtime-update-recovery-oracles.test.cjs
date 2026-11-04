// Pure observations/oracles ONLY. No browser/server/publication fixture created.
"use strict";
const test = require("node:test"),
 assert = require("node:assert/strict");
const O = require("../../scripts/test-runtime-update-recovery-ui.cjs");

test("required coverage cannot hide interrupted/old-offline/retry/new-offline skips behind broad PASS", () => {
 const all = Object.fromEntries(
  O.REQUIRED.map((key) => [key, { result: "PASS" }]),
 );
 assert.equal(O.completeCoverage(all), true);
 for (const key of O.REQUIRED) {
  assert.throws(() =>
   O.completeCoverage({ ...all, [key]: { result: "NOT RUN" } }),
  );
  const missing = { ...all };
  delete missing[key];
  assert.throws(() => O.completeCoverage(missing));
 }
});
test("static transport rejects traversal, query and absolute asset paths; native URLs are not rewritten", () => {
 assert.equal(
  O.relativeAsset("bundled-runtime/compiler-suite.zip"),
  "bundled-runtime/compiler-suite.zip",
 );
 for (const path of [
  "../secret",
  "%2e%2e/secret",
  "/index.html",
  "a/../b",
  "a\\b",
  "a//b",
  "index.html?fake=B",
  "a#fragment",
  "",
 ])
  assert.throws(() => O.relativeAsset(path));
});
test("P9/P10 oracle requires completed successful native phases of ONLY expected runtime profile", () => {
 const trace = (profile, tools = ["latex", "bibtex", "latex"]) => ({
  trace: tools.map((tool, i) => ({
   posted: true,
   entered: true,
   terminated: true,
   outcome: "native-return",
   nativeReturn: 0,
   tag: {
    profile,
    version: profile === 9 ? 14 : 17,
    engineId: "lualatex",
    tool,
    ordinal: i + 1,
    job: "same-job",
    request: "same-request",
    epoch: 7,
   },
  })),
  active: 0,
 });
 assert.equal(O.profileTrace(trace(9), 9), true);
 assert.equal(O.profileTrace(trace(10), 10), true);
 // Actual accepted pipeline shape: AUX changes after LaTeX3, so BibTeX4
 // is real additional native work, followed by final LaTeX5; not a duplicate.
 for (const profile of [9, 10]) {
  assert.equal(O.profileTrace(trace(profile, ["latex", "bibtex", "latex", "bibtex", "latex"]), profile), true);
  assert.equal(O.profileTrace(trace(profile, ["latex", "bibtex", "latex", "bibtex", "latex", "bibtex", "latex", "bibtex", "latex", "bibtex", "latex"]), profile), true);
 }
 for (const tools of [
  ["latex", "latex"], // zero BibTeX despite completed LaTeX
  ["bibtex", "latex", "latex"], // no producing LaTeX before BibTeX
  ["latex", "bibtex", "bibtex", "latex"], // two BibTeX phases from one pass
  ["latex", "bibtex", "bibtex", "bibtex", "latex"], // BibTeX exceeds LaTeX
  ["latex", "bibtex", "latex", "bibtex"], // no final LaTeX
  ["latex", "bibtex", "latex", "latex", "latex", "latex", "latex", "latex"], // seven LaTeX passes exceed production bound
 ]) assert.throws(() => O.profileTrace(trace(10, tools), 10));
 const wrongOrder = trace(10, ["latex", "bibtex", "latex", "bibtex", "latex"]);
 [wrongOrder.trace[2], wrongOrder.trace[3]] = [wrongOrder.trace[3], wrongOrder.trace[2]];
 assert.throws(() => O.profileTrace(wrongOrder, 10));
 assert.throws(() => O.profileTrace(trace(9), 10));
 assert.throws(() => O.profileTrace({ trace: [], active: 0 }, 10));
 for (const field of ["posted", "entered", "terminated"]) {
  const value = trace(10);
  value.trace[0][field] = false;
  assert.throws(() => O.profileTrace(value, 10));
 }
 const mixed = trace(10);
 mixed.trace.push(...trace(9).trace);
 assert.throws(() => O.profileTrace(mixed, 10));
 for (const patch of [
  { tool: "dvipdfmx" },
  { engineId: "pdftex" },
  { version: 6 },
  { version: 15 },
  { ordinal: 2 },
  { request: "another-request" },
  { job: "another-job" },
  { epoch: 8 },
 ]) {
  const bad = trace(10);
  Object.assign(bad.trace[0].tag, patch);
  assert.throws(() => O.profileTrace(bad, 10));
 }
 const noBib = trace(10);
 noBib.trace = noBib.trace.filter((t) => t.tag.tool !== "bibtex");
 assert.throws(() => O.profileTrace(noBib, 10));
 const failed = trace(10);
 failed.trace[0].nativeReturn = 1;
 assert.throws(() => O.profileTrace(failed, 10));
 const active = trace(10);
 active.active = 1;
 assert.throws(() => O.profileTrace(active, 10));
});
test("dirty activation refusal requires unchanged controller, head AND entire working value", () => {
 const before = {
  dirty: true,
  release: "A",
  head: "saved-A",
  value: "Україна🙂\n$1\\draft",
 };
 const after = { release: "A", head: "saved-A", value: before.value };
 assert.equal(
  O.dirtyRefusal(before, after, "Save your unsaved changes."),
  true,
 );
 for (const patch of [
  { release: "B" },
  { head: "new-head" },
  { value: "silently discarded" },
 ])
  assert.throws(() =>
   O.dirtyRefusal(before, { ...after, ...patch }, "Save first"),
  );
 assert.throws(() =>
  O.dirtyRefusal({ ...before, dirty: false }, after, "Save first"),
 );
 assert.throws(() => O.dirtyRefusal(before, after, ""));
});
test("interruption proof needs ONE real truncated transfer, not a complete/zero-byte/missing fetch", () => {
 const good = {
  requests: 1,
  expectedLength: O.PIN.B.length,
  actualPrefixBytes: 65536,
  connectionDestroyed: true,
 };
 assert.equal(O.cutProof(good, O.PIN.B.length), true);
 for (const patch of [
  { requests: 0 },
  { requests: 2 },
  { actualPrefixBytes: 0 },
  { actualPrefixBytes: O.PIN.B.length },
  { connectionDestroyed: false },
  { expectedLength: O.PIN.A.length },
 ])
  assert.throws(() => O.cutProof({ ...good, ...patch }, O.PIN.B.length));
});
test("inverse fixture line is derived from exact source BEFORE observing native result, not self-consistency", () => {
 const source =
  "% Україна🙂\n\\\\section{Main}\nPortable compiler fixture.\nbody\nIncluded file, reference\n";
 assert.equal(O.expectedInverseLine(source, "Portable compiler fixture."), 3);
 assert.equal(O.expectedInverseLine(source, "Included file, reference"), 5);
 assert.equal(
  O.expectedInverseLine(source, "Portable compiler fixture.", 3),
  3,
 );
 assert.throws(() =>
  O.expectedInverseLine(source, "Portable compiler fixture.", 4),
 );
 assert.throws(() => O.expectedInverseLine(source, "missing"));
 assert.throws(() =>
  O.expectedInverseLine(
   source + "Portable compiler fixture.",
   "Portable compiler fixture.",
  ),
 );
});
test("accepted compiler archive identities are real distinct immutable P9 and P10 APP archives", () => {
 assert.equal(O.PIN.A.profile, 9);
 assert.equal(O.PIN.B.profile, 10);
 assert.equal(O.PIN.A.length, 193549026);
 assert.equal(O.PIN.B.length, 193561168);
 assert.notEqual(O.PIN.A.sha256, O.PIN.B.sha256);
 assert.match(O.PIN.A.sha256, /^[a-f0-9]{64}$/);
 assert.match(O.PIN.B.sha256, /^[a-f0-9]{64}$/);
});
