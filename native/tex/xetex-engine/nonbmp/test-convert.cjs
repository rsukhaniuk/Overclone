"use strict";
// Real accepted XDV converter; optional map geometry is deliberately not claimed.
const fs = require("node:fs"), path = require("node:path"), assert = require("node:assert/strict");
const crypto = require("node:crypto");
const sha = bytes => crypto.createHash("sha256").update(bytes).digest("hex");
const out = process.env.RESULTS, input = process.env.XDV_INPUT, converter = process.env.CONVERTER;
assert.ok(out && input && converter);
(async () => {
  fs.mkdirSync(out, {recursive: true});
  const wasm = fs.readFileSync(path.join(converter, "oc-dvi.wasm"));
  const m = await require(path.join(converter, "oc-dvi.js"))({wasmBinary: wasm,
    print() { throw new Error("unexpected converter stdout"); },
    printErr() { throw new Error("unexpected converter stderr"); }});
  assert.equal(m._oc_dvi_abi_version(), 3);
  const alloc = bytes => { const p = m._malloc(bytes.length || 1); assert.ok(p); m.HEAPU8.set(bytes, p); return p; };
  const str = value => alloc(Buffer.from(value + "\0"));
  function add(name, bytes, resource) {
    const n = str(name), p = alloc(bytes);
    assert.equal(m._oc_dvi_add_file(n, p, bytes.length, resource), 0, name);
    m._free(n); m._free(p);
  }
  const pins = JSON.parse(fs.readFileSync("/inputs/suite/pins.json"));
  for (const item of pins) {
    if (/\.(wasm|js|fmt)$/.test(item.name) || item.name.includes("/")) continue;
    const bytes = fs.readFileSync("/inputs/suite/files/" + item.name);
    assert.equal(bytes.length, item.length); assert.equal(sha(bytes), item.sha256);
    add(item.name, bytes, 1);
  }
  if (process.env.PROJECT_FONTS === "1") {
    for (const [original, alias] of [["DejaVuSerif.ttf", "DejaVu 😀.ttf"],
      ["DejaVuSerif-Bold.ttf", "DejaVu Bold 😀.ttf"],
      ["DejaVuSerif-Italic.ttf", "DejaVu Italic 😀.ttf"],
      ["DejaVuSerif-BoldItalic.ttf", "DejaVu BoldItalic 😀.ttf"]])
      add("шрифти/" + alias, fs.readFileSync("/inputs/suite/files/" + original), 0);
  }
  const entry = "тези/розділ 😀.xdv", bytes = fs.readFileSync(input);
  add(entry, bytes, 0);
  const pointer = str(entry), rc = m._oc_dvi_run(pointer); m._free(pointer);
  const read = (p, n) => Buffer.from(m.HEAPU8.subarray(p, p + n));
  const pdf = read(m._oc_dvi_pdf_data(), m._oc_dvi_pdf_size());
  const log = read(m._oc_dvi_log_data(), m._oc_dvi_log_size());
  fs.writeFileSync(path.join(out, "converted.log"), log);
  assert.equal(rc, 0, log.toString());
  assert.ok(pdf.length > 1000 && pdf.subarray(0, 5).equals(Buffer.from("%PDF-")));
  fs.writeFileSync(path.join(out, "converted.pdf"), pdf);
  const receipt = {schemaVersion: 1, status: "pass", nativeReturn: rc, entry,
    actualInput: {length: bytes.length, sha256: sha(bytes)},
    output: {length: pdf.length, sha256: sha(pdf)},
    converter: {wasm: sha(wasm), loader: sha(fs.readFileSync(path.join(converter, "oc-dvi.js")))},
    projectFonts: process.env.PROJECT_FONTS === "1", geometryClaimed: false};
  fs.writeFileSync(path.join(out, "receipt.json"), JSON.stringify(receipt, null, 2));
  console.log(JSON.stringify(receipt));
})().catch(error => { console.error(error); process.exitCode = 1; });
