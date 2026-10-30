// Test tooling only: canonical-alias rejection followed by genuine LuaTeX
// opening the surviving root/include/font bytes, in both insertion orders.
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
);
fs.mkdirSync(results, { recursive: true });
const record = (name, file) => {
  const bytes = fs.readFileSync(file);
  return { name, file, length: bytes.length, sha256: sha(bytes) };
};
const json = (name, data) => {
  const file = path.join(results, name);
  fs.writeFileSync(file, JSON.stringify(data, null, 2) + "\n");
  return file;
};
const readJson = (file) => {
  try {
    return JSON.parse(fs.readFileSync(file));
  } catch (error) {
    throw new Error("Invalid path receipt: " + file, { cause: error });
  }
};
const source = path.join(__dirname, "tests/decomposed-paths.tex");
const observer = path.join(__dirname, "tests/decomposed-paths.lua");
const font = path.join(process.env.SUITE_INPUT, "files/DejaVuSerif.ttf");
const chapter = path.join(results, "path-chapter.tex");
fs.writeFileSync(
  chapter,
  "\\section{Canonical path marker}\nIncluded exact source bytes.\n",
);
const attempts = [];
for (const composedFirst of [false, true]) {
  const first = "и\u0306-e\u0301",
    second = first.normalize("NFC");
  const spell = composedFirst ? second : first,
    alias = composedFirst ? first : second;
  const root = "тези/" + spell + ".tex",
    include = "розділи/" + spell + ".tex",
    fontname = "fonts/" + spell + ".ttf";
  const label =
    "abi3-path-" + (composedFirst ? "composed-first" : "decomposed-first");
  const project = json(label + ".project.inputs.json", {
    files: [
      record(include, chapter),
      record("path-observer.lua", observer),
      record("expected-chapter.bin", chapter),
      record("expected-font.bin", font),
    ],
  });
  const runtime = json(label + ".runtime.inputs.json", {
    files: [record(fontname, font)],
  });
  const aliases = json(label + ".aliases.inputs.json", [
    { kind: 1, original: root, alias: "тези/" + alias + ".tex" },
    { kind: 1, original: include, alias: "розділи/" + alias + ".tex" },
    { kind: 0, original: fontname, alias: "fonts/" + alias + ".ttf" },
  ]);
  const result = spawnSync(
    process.execPath,
    [path.join(__dirname, "test-engine.cjs"), "latex", label],
    {
      env: {
        ...process.env,
        ROOT_NAME: root,
        SOURCE_FILE: source,
        PROJECT_MANIFEST: project,
        RUNTIME_MANIFEST: runtime,
        PATH_ALIAS_MANIFEST: aliases,
      },
      encoding: "utf8",
      timeout: 30000,
      maxBuffer: 2 * 1024 * 1024,
    },
  );
  fs.writeFileSync(
    path.join(results, label + ".run.log"),
    (result.stdout || "") + (result.stderr || ""),
  );
  assert.ifError(result.error);
  assert.equal(result.status, 0, label + ": inspect run log");
  const receipt = readJson(path.join(results, label + ".json"));
  assert.equal(receipt.status, 0);
  assert.equal(receipt.budget.failed, false);
  assert.equal(receipt.rootUtf8Hex, Buffer.from(root).toString("hex"));
  assert.equal(receipt.sourceSha256, sha(fs.readFileSync(source)));
  assert.equal(receipt.pathAliases.length, 3);
  const consoleText = fs.readFileSync(
    path.join(results, label + ".console.txt"),
    "utf8",
  );
  const fls = fs.readFileSync(path.join(results, label + ".fls"), "utf8");
  for (const [kind, name] of [
    ["ROOT", "/work/project/" + root],
    ["INCLUDE", "/work/project/" + include],
    ["FONT", "/work/runtime/" + fontname],
  ]) {
    assert.ok(consoleText.includes("EXACT-PATH-" + kind + " " + name), name);
    assert.ok(fls.includes(name), "genuine engine opened exact " + name);
  }
  attempts.push({
    label,
    root,
    include,
    fontname,
    sourceSha256: receipt.sourceSha256,
    loaderSha256: receipt.loaderSha256,
    wasmSha256: receipt.wasmSha256,
    formatSha256: receipt.formatSha256,
    pdf: receipt.artifacts.pdf,
  });
}
const receipt = {
  schemaVersion: 1,
  passed: true,
  scope:
    "Genuine ABI3 engine; unique decomposed paths retained and canonical aliases rejected before native entry in both orders; no universal .NET casing parity claim",
  attempts,
};
json("abi3-paths.json", receipt);
console.log(JSON.stringify(receipt));
