// Native-fixture tooling only; no application orchestration or synthetic maps.
"use strict";
const fs = require("node:fs");
const path = require("node:path");
const crypto = require("node:crypto");
const assert = require("node:assert/strict");
const sha = (b) => crypto.createHash("sha256").update(b).digest("hex");
const candidate = process.env.CANDIDATE || "/control/r2-lua-maps/candidate";
const suite = process.env.SUITE_INPUT || "/inputs/suite";
const format =
  process.env.FORMAT_FILE || path.join(suite, "files/lualatex.fmt");
async function load(
  root,
  lines = [],
  expectedAbi = Number(process.env.LUA_EXPECTED_ABI || 4),
) {
  const wasmBytes = fs.readFileSync(path.join(candidate, "luatex.wasm"));
  const create = require(path.join(candidate, "luatex.js"));
  const m = await create({
    noInitialRun: true,
    instantiateWasm(imports, done) {
      const i = new WebAssembly.Instance(
        new WebAssembly.Module(wasmBytes),
        imports,
      );
      done(i);
      return i.exports;
    },
    print: (line) => {
      lines.push(line);
      if (line.includes("RUNAWAY-BEGIN"))
        require("node:worker_threads").parentPort?.postMessage("runaway");
    },
    printErr: (line) => lines.push(line),
  });
  const call = (n, types = [], args = [], result = "number") =>
    m.ccall(n, result, types, args);
  assert.ok(expectedAbi === 3 || expectedAbi === 4 || expectedAbi === 5);
  assert.equal(call("oc_luatex_abi"), expectedAbi);
  if (expectedAbi === 4 || expectedAbi === 5)
    assert.equal(call("oc_synctex_abi"), 0x00010000);
  assert.equal(
    call(
      "oc_luatex_configure",
      ["string", "string"],
      [root, "r2-native-synthetic"],
    ),
    0,
  );
  const seed = (kind, name, bytes) => {
    const p = m._malloc(bytes.length || 1);
    m.HEAPU8.set(bytes, p);
    const rc = call(
      "oc_luatex_add_file",
      ["number", "string", "number", "number"],
      [kind, name, p, bytes.length],
    );
    m._free(p);
    assert.equal(rc, 0, name);
  };
  const pinsBytes = fs.readFileSync(path.join(suite, "pins.json"));
  let pins;
  try {
    pins = JSON.parse(pinsBytes);
  } catch (error) {
    throw new Error("Invalid supplied suite manifest", { cause: error });
  }
  for (const p of pins) {
    if (/\.(js|wasm|fmt)$/.test(p.name) || p.name.startsWith("notices/"))
      continue;
    const b = fs.readFileSync(path.join(suite, "files", p.name));
    assert.equal(b.length, p.length, p.name);
    assert.equal(sha(b), p.sha256, p.name);
    seed(0, p.name, b);
  }
  const fmt = fs.readFileSync(format);
  seed(0, "lualatex.fmt", fmt);
  const read = (sizeName, copyName, tag, max = 1048575) => {
    const n = call(sizeName, ["number"], [tag]);
    if (!n) return Buffer.alloc(0);
    assert.ok(n > 0 && n <= max);
    const p = m._malloc(n);
    assert.equal(
      call(copyName, ["number", "number", "number"], [tag, p, n]),
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
    read,
    identities: {
      candidate: sha(wasmBytes),
      loader: sha(fs.readFileSync(path.join(candidate, "luatex.js"))),
      suitePins: sha(pinsBytes),
      format: sha(fmt),
    },
  };
}
module.exports = { load, sha, fs, path, assert, candidate };
