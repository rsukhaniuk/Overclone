// Test tooling only: genuine LuaTeX producer and fresh native carry observer.
// Requires explicit integrity-pinned suite data and format, never downloads.
"use strict";
const fs = require("node:fs");
const path = require("node:path");
const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const { spawnSync } = require("node:child_process");
const sha = (bytes) => crypto.createHash("sha256").update(bytes).digest("hex");
const results = process.env.RESULTS;
assert.ok(
  results &&
    process.env.SUITE_INPUT &&
    process.env.SUITE_PINS_SHA256 &&
    process.env.FORMAT_FILE,
  "Set RESULTS, SUITE_INPUT, SUITE_PINS_SHA256 and FORMAT_FILE explicitly",
);
fs.mkdirSync(results, { recursive: true });
const existing = path.resolve(__dirname, "../package-compatibility/fixtures");
const reference = path.join(
  existing,
  "biblatex-bibtex.tex.files/references.bib",
);
const record = (name, file) => {
  const bytes = fs.readFileSync(file);
  return { name, file, length: bytes.length, sha256: sha(bytes) };
};
const manifest = (label, files) => {
  const file = path.join(results, label + ".inputs.json");
  fs.writeFileSync(file, JSON.stringify({ files }, null, 2) + "\n");
  return file;
};
const readJson = (file) => {
  try {
    return JSON.parse(fs.readFileSync(file));
  } catch (error) {
    throw new Error("Invalid native test receipt: " + file, { cause: error });
  }
};
const run = (label, source, extra = {}) => {
  const env = {
    ...process.env,
    SOURCE_FILE: source,
    PROJECT_MANIFEST: manifest(label, [record("references.bib", reference)]),
    OUTPUT_NAMES: "result-blx.bib,result.bcf,result.run.xml",
    ...extra,
  };
  const result = spawnSync(
    process.execPath,
    [path.join(__dirname, "test-engine.cjs"), "latex", label],
    { env, encoding: "utf8", timeout: 30000, maxBuffer: 2 * 1024 * 1024 },
  );
  fs.writeFileSync(
    path.join(results, label + ".run.log"),
    (result.stdout || "") + (result.stderr || ""),
  );
  assert.ifError(result.error);
  assert.equal(result.status, 0, label + ": inspect " + label + ".run.log");
  const receipt = readJson(path.join(results, label + ".json"));
  assert.equal(receipt.status, 0);
  assert.equal(receipt.budget.failed, false);
  assert.equal(
    receipt.sourceSha256,
    sha(fs.readFileSync(source)),
    "fixture source is unchanged",
  );
  return receipt;
};
const output = (receipt, name) => {
  assert.ok(
    receipt.artifacts[name]?.length > 0,
    "genuine requested output " + name,
  );
  const artifact = receipt.artifacts[name];
  const file = path.join(results, artifact.file),
    bytes = fs.readFileSync(file);
  assert.equal(bytes.length, artifact.length);
  assert.equal(sha(bytes), artifact.sha256);
  return { file, bytes };
};
const bibtexSource = path.join(existing, "biblatex-bibtex.tex");
const biberSource = path.join(__dirname, "tests/biblatex-biber.tex");
const bibtex = run("abi3-biblatex-bibtex", bibtexSource);
const blx = output(bibtex, "result-blx.bib");
assert.match(blx.bytes.toString(), /@Control\s*\{biblatex-control/);
assert.match(blx.bytes.toString(), /biblatex/);
assert.match(
  fs.readFileSync(path.join(results, "abi3-biblatex-bibtex.aux"), "utf8"),
  /\\bibdata\{result-blx,references\}/,
);
const biber = run("abi3-biblatex-biber", biberSource);
const bcf = output(biber, "result.bcf"),
  runxml = output(biber, "result.run.xml");
assert.match(bcf.bytes.toString(), /<bcf:controlfile\b/);
assert.match(bcf.bytes.toString(), /bltxversion="3\.19"/);
assert.match(
  bcf.bytes.toString(),
  /<bcf:citekey[^>]*>reference2<\/bcf:citekey>/,
);
assert.match(bcf.bytes.toString(), />references\.bib<\/bcf:datasource>/);
assert.match(runxml.bytes.toString(), /<binary>biber<\/binary>/);
assert.match(runxml.bytes.toString(), /result\.bcf/);
const names = ["result-blx.bib", "result.bcf", "result.run.xml"];
const produced = [blx, bcf, runxml];
const carry = manifest(
  "abi3-carry",
  names.map((name, i) => record(name, produced[i].file)),
);
const expected = manifest(
  "abi3-observer",
  produced.map((entry, i) => record("expected-" + (i + 1), entry.file)),
);
const observer = run(
  "abi3-carry-observer",
  path.join(__dirname, "tests/carry-observer.tex"),
  { PROJECT_MANIFEST: expected, CARRY_MANIFEST: carry },
);
const consoleText = fs.readFileSync(
  path.join(results, "abi3-carry-observer.console.txt"),
  "utf8",
);
for (let i = 0; i < names.length; ++i) {
  const name = names[i];
  assert.equal(observer.carryInputs[name].sha256, sha(produced[i].bytes));
  assert.deepEqual(
    output(observer, name).bytes,
    produced[i].bytes,
    "exact fresh-module getter roundtrip",
  );
  assert.ok(
    consoleText.includes("ABI3-CARRY-EXACT " + name),
    "actual Lua read/comparison " + name,
  );
}
// A second unchanged real biblatex attempt consumes native control/AUX carry.
const secondCarry = manifest("abi3-bibtex-pass2", [
  record("result-blx.bib", blx.file),
  record("result.aux", path.join(results, "abi3-biblatex-bibtex.aux")),
]);
const second = run("abi3-biblatex-bibtex-pass2", bibtexSource, {
  CARRY_MANIFEST: secondCarry,
});
assert.deepEqual(output(second, "result-blx.bib").bytes, blx.bytes);
const receipt = {
  schemaVersion: 1,
  passed: true,
  scope:
    "Lua ABI3 native producer/carry only; no BibTeX/Biber execution, resolved citation, registered app or offline claim",
  producerLabels: [bibtex.label, biber.label],
  carryLabels: [observer.label, second.label],
  names: Object.fromEntries(
    names.map((name, i) => [
      name,
      { length: produced[i].bytes.length, sha256: sha(produced[i].bytes) },
    ]),
  ),
  loaderSha256: observer.loaderSha256,
  wasmSha256: observer.wasmSha256,
  formatSha256: observer.formatSha256,
  suitePinsSha256: process.env.SUITE_PINS_SHA256,
};
fs.writeFileSync(
  path.join(results, "abi3-bibliography.json"),
  JSON.stringify(receipt, null, 2) + "\n",
);
console.log(JSON.stringify(receipt));
