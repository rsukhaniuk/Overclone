"use strict";
const { fs, path, assert, hashBytes, engine } = require("./runtime.cjs");
const out = process.env.RESULTS;
assert.ok(out);
(async () => {
 fs.mkdirSync(out, { recursive: true });
 let checks = 0;
 const eq = (a, b) => {
  assert.deepEqual(a, b);
  ++checks;
 };
 const configCases = [];
 for (const args of [
  [2, 1, 1],
  [0, 1048576, 1],
  [1, 0, 1],
  [1, 1048576, 1],
  [1, 100, 0],
  [0, 0, 0],
  [-1, 1, 1],
  [1, -1, 1],
 ]) {
  const { call } = await engine([], false, false);
  eq(call("oc_mem_configure", ["number", "number", "number"], args), 0);
  eq(call("oc_synctex_status"), 2);
  eq(
   call("oc_mem_configure", ["number", "number", "number"], [1, 1048575, 601]),
   0,
  );
  for (const tag of [0, 1, 601])
   eq(call("oc_synctex_length", ["number"], [tag]), 0);
  configCases.push({ args, status: "pass" });
 }
 for (const name of [
  "",
  "../escape.tex",
  "/host.tex",
  "a/../b.tex",
  "a//b.tex",
  "a\\b.tex",
  "bad%name.tex",
  "a\u0001.tex",
  "головний 😀.tex",
 ]) {
  const { call } = await engine([], false, false);
  eq(call("setMainEntry", ["string"], [name]), -2);
  eq(call("oc_synctex_length", ["number"], [601]), 0);
 }
 const map = fs.readFileSync(process.env.MAP_FILE);
 const queryPath = "/inputs/suite/files";
 const wasm = fs.readFileSync(path.join(queryPath, "synctex-query.wasm"));
 const create = require(path.join(queryPath, "synctex-query.js"));
 async function query() {
  return create({
   noInitialRun: true,
   print() {},
   printErr() {},
   instantiateWasm(imports, done) {
    const i = new WebAssembly.Instance(new WebAssembly.Module(wasm), imports);
    done(i);
    return i.exports;
   },
  });
 }
 const put = (m, b) => {
  const p = m._malloc(b.length || 1);
  m.HEAPU8.set(b, p);
  return p;
 };
 const cases = [
  ["empty", Buffer.alloc(0)],
  ["magic", Buffer.from("SyncTeX Version:9\n")],
  ["nul", Buffer.concat([map, Buffer.from([0])])],
  ["oversized", Buffer.alloc(1048576, 65)],
  ["truncated-half", map.subarray(0, Math.floor(map.length / 2))],
 ];
 const queryCases = [];
 for (const [name, b] of cases) {
  const m = await query(),
   p = put(m, b);
  const rc = m._sq_load(p, b.length);
  m._free(p);
  if (name !== "truncated-half") {
   assert.notEqual(rc, 0);
   ++checks;
  }
  eq(m._sq_result_length(), 0);
  eq(m._sq_close(), 0);
  queryCases.push({
   name,
   rc,
   scope:
    name === "truncated-half"
     ? "Observed upstream complete-format behavior; not a new parser promise"
     : "Strict unchanged ABI1 admission guard",
  });
 }
 const m = await query(),
  p = put(m, map);
 eq(m._sq_load(p, map.length), 0);
 m._free(p);
 const file = Buffer.from("розділ/my thesis.tex"),
  f = put(m, file);
 eq(m._sq_forward(f, file.length, 10, 0, -1), 0);
 m._free(f);
 const n = m._sq_result_length();
 assert.ok(n > 0 && n <= 32768);
 ++checks;
 const dst = m._malloc(n);
 eq(m._sq_result_copy(dst, n), 0);
 let result;
 try {
  result = JSON.parse(Buffer.from(m.HEAPU8.subarray(dst, dst + n)));
 } catch (e) {
  throw new Error("invalid real query result", { cause: e });
 }
 m._free(dst);
 eq(result.schema, 1);
 assert.ok(
  result.matches.some((x) => x.file === "розділ/my thesis.tex" && x.page === 1),
 );
 ++checks;
 const bad = put(m, Buffer.from("bogus"));
 assert.notEqual(m._sq_load(bad, 5), 0);
 ++checks;
 eq(m._sq_result_length(), 0);
 m._free(bad);
 eq(m._sq_close(), 0);
 assert.notEqual(m._sq_inverse(1, 100, 100), 0);
 ++checks;
 eq(m._sq_result_length(), 0);
 const receipt = {
  schemaVersion: 1,
  status: "pass",
  checks,
  skips: 0,
  configuration: configCases,
  queryCases,
  actualForward: result,
  queryWasmSha256: hashBytes(wasm),
  rawMapSha256: hashBytes(map),
  note:
   "Raw producer can seal only its native full postamble/actual close/success. The existing query parser ABI is unchanged and does not promise whole-format canonical validation.",
 };
 fs.writeFileSync(
  path.join(out, "config-query.json"),
  JSON.stringify(receipt, null, 2) + "\n",
 );
 console.log(JSON.stringify(receipt));
})().catch((e) => {
 console.error(e);
 process.exitCode = 1;
});
