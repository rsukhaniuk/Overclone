"use strict";
const { fs, path, sha, assert, candidate } = require("./runtime.cjs");
const wasmBytes = fs.readFileSync(path.join(candidate, "luatex.wasm"));
const wasm = new WebAssembly.Module(wasmBytes);
const create = require(path.join(candidate, "luatex.js"));
(async () => {
  let checks = 0;
  const cases = [];
  const exported = WebAssembly.Module.exports(wasm).map((e) => e.name);
  for (const name of [
    "oc_synctex_finish",
    "oc_synctex_begin",
    "oc_mem_stage_checked",
    "oc_mem_abort",
    "oc_mem_open",
    "oc_mem_printf",
    "oc_synctex_pdf_close",
    "oc_lua_maps_ship_page",
    "oc_lua_maps_eligible",
    "oc_lua_sink_copy",
    "oc_lua_geometry_begin",
    "oc_lua_geometry_end",
    "oc_lua_geometry_observe",
    "oc_lua_geometry_unsafe",
    "oc_lua_geometry_attributes",
  ]) {
    assert.ok(!exported.includes(name) && !exported.includes("_" + name), name);
    ++checks;
  }
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
    const m = await create({
      noInitialRun: true,
      print() {},
      printErr() {},
      instantiateWasm(imports, done) {
        const i = new WebAssembly.Instance(wasm, imports);
        done(i);
        return i.exports;
      },
    });
    assert.equal(m._oc_mem_configure(...args), 0);
    ++checks;
    assert.equal(m._oc_synctex_status(), 2);
    ++checks;
    assert.equal(m._oc_mem_configure(1, 1048575, 401), 0);
    ++checks;
    for (const tag of [0, 1, 401]) {
      assert.equal(m._oc_synctex_length(tag), 0);
      ++checks;
      assert.equal(m._oc_synctex_geometry_length(tag), 0);
      ++checks;
      const p = m._malloc(8);
      m.HEAPU8.fill(0x5a, p, p + 8);
      for (const n of ["_oc_synctex_copy", "_oc_synctex_geometry_copy"]) {
        assert.equal(m[n](tag, p, 8), 0);
        ++checks;
        assert.equal(
          m.HEAPU8.subarray(p, p + 8).every((b) => b === 0x5a),
          true,
        );
        ++checks;
      }
      m._free(p);
    }
    cases.push({ args, status: "pass" });
  }
  const result = {
    schemaVersion: 1,
    status: "pass",
    checks,
    skips: 0,
    cases,
    wasmSha256: sha(wasmBytes),
    exported,
  };
  const out = process.env.RESULTS || "/control/r2-lua-maps/evidence";
  fs.mkdirSync(out, { recursive: true });
  fs.writeFileSync(
    path.join(out, "config.json"),
    JSON.stringify(result, null, 2) + "\n",
  );
  console.log(JSON.stringify(result));
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
