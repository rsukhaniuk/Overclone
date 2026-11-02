"use strict";
// Actual compiled converter only. Synthetic malformed controls mutate the native
// intermediate explicitly and are never counted as same-pass positive outputs.
const { fs, path, assert, hashBytes } = require("./runtime.cjs");
const candidate =
 process.env.CONVERTER || "/control/r2-xe-maps/build/converter-first";
const out = process.env.RESULTS || "/control/r2-xe-maps/evidence/converted";
const input = process.env.XDV_INPUT;
assert.ok(input, "explicit actual XDV fixture required");
const label = process.argv[2] || "positive",
 mode = process.env.CONVERT_SCENARIO || label,
 tag = 602;
(async () => {
 fs.mkdirSync(out, { recursive: true });
 let checks = 0;
 const eq = (a, b) => {
  assert.deepEqual(a, b);
  ++checks;
 };
 const wasm = fs.readFileSync(path.join(candidate, "oc-dvi.wasm"));
 const m = await require(path.join(candidate, "oc-dvi.js"))({
  wasmBinary: wasm,
  print() {
   throw new Error("unexpected native stdout");
  },
  printErr() {
   throw new Error("unexpected native stderr");
  },
 });
 eq(m._oc_dvi_abi_version(), 3);
 const exported = WebAssembly.Module.exports(new WebAssembly.Module(wasm)).map(
  (x) => x.name,
 );
 for (const name of [
  "oc_dvi_geo_units",
  "oc_dvi_geo_begin",
  "oc_dvi_geo_end",
  "oc_dvi_geo_special",
  "oc_dvi_geo_put",
  "oc_dvi_geo_special_end",
 ]) {
  assert.ok(!exported.includes(name));
  ++checks;
 }
 const alloc = (b) => {
  const p = m._malloc(b.length || 1);
  assert.ok(p);
  m.HEAPU8.set(b, p);
  return p;
 };
 const str = (s) => alloc(Buffer.from(s + "\0"));
 const add = (name, b, resource) => {
  const n = str(name),
   p = alloc(b);
  eq(m._oc_dvi_add_file(n, p, b.length, resource), 0);
  if (mode === "mutate-caller-buffer") m.HEAPU8.fill(0, p, p + b.length);
  m._free(n);
  m._free(p);
 };
 const pinsBytes = fs.readFileSync("/inputs/suite/pins.json");
 let pins;
 try {
  pins = JSON.parse(pinsBytes);
 } catch (e) {
  throw new Error("bad suite pins", { cause: e });
 }
 if (mode !== "missing-resources")
  for (const p of pins) {
   if (/\.(wasm|js|fmt)$/.test(p.name) || p.name.includes("/")) continue;
   const b = fs.readFileSync("/inputs/suite/files/" + p.name);
   eq(b.length, p.length);
   eq(hashBytes(b), p.sha256);
   add(p.name, b, 1);
  }
 let dvi = fs.readFileSync(input);
 if (mode === "empty") dvi = Buffer.alloc(0);
 if (mode === "bad-header") {
  dvi = Buffer.from(dvi);
  dvi[0] = 0;
 }
 if (mode === "truncated") dvi = dvi.subarray(0, Math.floor(dvi.length / 2));
 if (mode === "bad-postamble" || mode === "unknown-unit") {
  dvi = Buffer.from(dvi);
  let p = dvi.length - 1;
  while (dvi[p] === 223) --p;
  const post = dvi.readUInt32BE(p - 4);
  if (mode === "bad-postamble") dvi[post] = 0;
  else {
   dvi.writeUInt32BE(12700000, 2);
   dvi.writeUInt32BE(12700000, post + 5);
  }
 }
 const given = Buffer.from(dvi),
  entry = "розділ/converted thesis.xdv";
 add(entry, dvi, 0);
 if (mode === "mutate-caller-buffer") dvi.fill(0);
 // A project result.pdf cannot masquerade as the separate native output buffer.
 add("result.pdf", Buffer.from("%PDF-1.7\nSTALE-PROJECT-BYTES"), 0);
 eq(m._oc_dvi_geometry_length(tag), 0);
 eq(m._oc_dvi_geometry_size(), 0);
 if (mode !== "default-off")
  eq(m._oc_dvi_geometry_configure(mode === "off" ? 0 : 1, tag), 1);
 if (mode === "invalid-config") eq(m._oc_dvi_geometry_configure(2, tag), 0);
 if (mode === "repeat-config") eq(m._oc_dvi_geometry_configure(1, tag), 0);
 const name = str(entry),
  rc = m._oc_dvi_run(name);
 m._free(name);
 const read = (p, n) => Buffer.from(m.HEAPU8.subarray(p, p + n));
 const pdf = read(m._oc_dvi_pdf_data(), m._oc_dvi_pdf_size()),
  log = read(m._oc_dvi_log_data(), m._oc_dvi_log_size());
 const geometry = read(m._oc_dvi_geometry_data(), m._oc_dvi_geometry_size());
 for (const [ext, b] of [
  ["pdf", pdf],
  ["log", log],
  ["geometry.json", geometry],
 ])
  fs.writeFileSync(path.join(out, label + "." + ext), b);
 const failed = [
  "empty",
  "bad-header",
  "truncated",
  "bad-postamble",
  "missing-resources",
 ].includes(mode);
 const noGeometry =
  failed ||
  [
   "off",
   "default-off",
   "invalid-config",
   "repeat-config",
   "pages513",
  ].includes(mode);
 if (failed) {
  assert.notEqual(rc, 0);
  ++checks;
  eq(pdf.length, 0);
 } else {
  eq(rc, 0);
  assert.ok(pdf.length > 1000);
  ++checks;
 }
 let g = null;
 if (noGeometry) eq(geometry.length, 0);
 else {
  assert.ok(geometry.length > 0 && geometry.length <= 131072);
  ++checks;
  try {
   g = JSON.parse(geometry);
  } catch (e) {
   throw new Error("invalid native sidecar", { cause: e });
  }
  eq(g.schemaVersion, 1);
  eq(
   g.pages.length,
   Number(process.env.EXPECTED_PAGES || (mode === "pages512" ? 512 : 4)),
  );
  const unsupported = [
   "unknown-unit",
   "unsafe",
   "media-override",
   "dict-injection",
   "rotate-name",
   "crop-name",
   "noninvertible",
   "bbox-program",
  ].includes(mode);
  for (const [i, p] of g.pages.entries()) {
   eq(p.page, i + 1);
   if (unsupported || (mode === "bounds" && i === 0))
    eq(p, {
     page: i + 1,
     status: "unsupported",
     reason: "out-of-domain",
     writerToPdfRaw: null,
     writerPageBoxBp: null,
    });
   else {
    eq(p.status, "available");
    eq(p.reason, "none");
    eq(p.writerToPdfRaw.length, 6);
    eq(p.writerPageBoxBp.length, 4);
    assert.ok(
     [...p.writerToPdfRaw, ...p.writerPageBoxBp].every(Number.isFinite),
    );
    ++checks;
   }
  }
  eq(m._oc_dvi_geometry_length(tag), geometry.length);
  for (const t of [0, tag - 1, tag + 1]) eq(m._oc_dvi_geometry_length(t), 0);
  const p = alloc(Buffer.alloc(geometry.length, 0x5a));
  for (const [token, capacity] of [
   [tag, geometry.length - 1],
   [tag + 1, geometry.length],
   [tag, 131073],
  ]) {
   eq(m._oc_dvi_geometry_copy(token, p, capacity), 0);
   eq(
    m.HEAPU8.subarray(p, p + geometry.length).every((x) => x === 0x5a),
    true,
   );
  }
  eq(m._oc_dvi_geometry_copy(tag, p, geometry.length), geometry.length);
  eq(read(p, geometry.length), geometry);
  m._free(p);
 }
 // Every ineligible later operation hides the previously sealed native sidecar.
 if (mode === "post-config") eq(m._oc_dvi_geometry_configure(1, tag), 0);
 else if (mode === "api-error") {
  const n = str("late.dvi");
  eq(m._oc_dvi_add_file(n, 0, 0, 0), 3);
  m._free(n);
 } else eq(m._oc_dvi_run(0), 3);
 eq(m._oc_dvi_geometry_size(), 0);
 eq(m._oc_dvi_geometry_length(tag), 0);
 eq(m._oc_dvi_pdf_size(), 0);
 const receipt = {
  schemaVersion: 1,
  status: "pass",
  label,
  mode,
  nativeReturn: m._oc_dvi_native_return(),
  result: rc,
  checks,
  skips: 0,
  input: { length: given.length, sha256: hashBytes(given) },
  inputMutatedControl: given.equals(fs.readFileSync(input)) ? false : true,
  callerBufferMutatedAfterCopy: mode === "mutate-caller-buffer",
  candidate: {
   wasm: hashBytes(wasm),
   loader: hashBytes(fs.readFileSync(path.join(candidate, "oc-dvi.js"))),
  },
  suitePinsSha256: hashBytes(pinsBytes),
  outputs: {
   pdf: { length: pdf.length, sha256: hashBytes(pdf) },
   geometry: { length: geometry.length, sha256: hashBytes(geometry) },
  },
 };
 fs.writeFileSync(
  path.join(out, label + ".json"),
  JSON.stringify(receipt, null, 2) + "\n",
 );
 console.log(JSON.stringify(receipt));
})().catch((e) => {
 console.error(e);
 process.exitCode = 1;
});
