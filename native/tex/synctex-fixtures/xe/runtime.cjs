"use strict";
// Test tooling only; genuine factory/native calls, never product-worker policy.
const fs = require("node:fs"),
 path = require("node:path"),
 crypto = require("node:crypto"),
 assert = require("node:assert/strict");
const hashBytes = (b) => crypto.createHash("sha256").update(b).digest("hex");
async function engine(lines = [], format = true, seedRuntime = true) {
 const candidate =
  process.env.ENGINE || "/control/r2-xe-maps/build/engine-third";
 const wasm = fs.readFileSync(path.join(candidate, "engine.wasm"));
 const m = await require(path.join(candidate, "engine.js"))({
  wasmBinary: wasm,
  print: (s) => {
   lines.push(s);
   if (s.includes("RUNAWAY-BEGIN"))
    require("node:worker_threads").parentPort?.postMessage("runaway");
  },
  printErr: (s) => lines.push(s),
 });
 const call = (n, t = [], a = []) => m.ccall(n, "number", t, a);
 assert.equal(call("oc_xe_maps_abi"), 1);
 assert.equal(call("oc_synctex_abi"), 65536);
 m.FS.mkdir("/work");
 m.FS.mkdir("/tex");
 m.FS.chdir("/work");
 const seed = (n, b) => {
  m.FS.mkdirTree(path.posix.dirname(n));
  m.FS.writeFile(n, b);
 };
 const pinsBytes = fs.readFileSync("/inputs/suite/pins.json");
 let pins;
 try {
  pins = JSON.parse(pinsBytes);
 } catch (e) {
  throw new Error("bad suite pins", { cause: e });
 }
 if (seedRuntime)
  for (const p of pins) {
   if (/\.(wasm|js|fmt)$/.test(p.name) || p.name.startsWith("notices/"))
    continue;
   const b = fs.readFileSync("/inputs/suite/files/" + p.name);
   assert.equal(b.length, p.length);
   assert.equal(hashBytes(b), p.sha256);
   seed("/tex/" + p.name, b);
  }
 let fmt = null;
 if (format) {
  fmt = fs.readFileSync(
   process.env.ENGINE_FORMAT || "/control/r2-xe-maps/build/xelatex.fmt",
  );
  seed("/tex/xelatex.fmt", fmt);
 }
 const map = (tag) => {
  const n = call("oc_synctex_length", ["number"], [tag]);
  if (!n) return Buffer.alloc(0);
  assert.ok(n <= 1048575);
  const p = m._malloc(n);
  assert.equal(
   call("oc_synctex_copy", ["number", "number", "number"], [tag, p, n]),
   n,
  );
  const b = Buffer.from(m.HEAPU8.subarray(p, p + n));
  m._free(p);
  return b;
 };
 return {
  m,
  call,
  seed,
  map,
  identities: {
   wasm: hashBytes(wasm),
   loader: hashBytes(fs.readFileSync(path.join(candidate, "engine.js"))),
   format: fmt ? hashBytes(fmt) : null,
   suitePins: hashBytes(pinsBytes),
  },
 };
}
module.exports = { fs, path, assert, hashBytes, engine };
