"use strict";
const fs = require("node:fs"),
 path = require("node:path"),
 assert = require("node:assert/strict"),
 crypto = require("node:crypto");
const sha = (b) => crypto.createHash("sha256").update(b).digest("hex");
(async () => {
 const out = process.env.RESULTS;
 assert.ok(out && !fs.existsSync(out));
 fs.mkdirSync(out, { recursive: true });
 const rows = [];
 for (const kind of ["lua", "dvi", "xe"])
  for (const generation of ["graphics1", "origin1"]) {
   const directory =
    generation === "origin1"
     ? process.env[
        kind === "lua"
         ? "CANDIDATE"
         : kind === "dvi"
           ? "DVI_CONVERTER"
           : "XE_CONVERTER"
       ]
     : "/control/r2-graphics-navigation/candidate/" +
       (kind === "lua" ? "lua-graphics4" : kind + "-graphics1");
   const name = kind === "lua" ? "luatex" : "oc-dvi",
    wasm = fs.readFileSync(path.join(directory, name + ".wasm")),
    loader = fs.readFileSync(path.join(directory, name + ".js"));
   let calls = 0;
   const m = await require(path.join(directory, name + ".js"))({
    noInitialRun: true,
    instantiateWasm(imports, done) {
     ++calls;
     const i = new WebAssembly.Instance(new WebAssembly.Module(wasm), imports);
     done(i);
     return i.exports;
    },
   });
   assert.equal(calls, 1);
   const base = kind === "lua" ? m._oc_luatex_abi() : m._oc_dvi_abi_version();
   assert.equal(base, kind === "lua" ? 5 : kind === "dvi" ? 2 : 3);
   assert.equal(
    kind === "lua" ? m._oc_lua_graphics_abi() : m._oc_dvi_graphics_abi(),
    1,
   );
   const capability =
    m[kind === "lua" ? "_oc_lua_origin_abi" : "_oc_dvi_origin_abi"];
   if (generation === "origin1") assert.equal(capability(), 1);
   else
    assert.equal(
     typeof capability,
     "undefined",
     "old base ABI + graphics1 must NOT imply origin support",
    );
   for (const n of [
    "oc_dvi_origin_unsafe",
    "oc_dvi_origin_key",
    "oc_dvi_origin_final",
    "oc_lua_origin_page",
    "oc_lua_origin_root_begin",
    "oc_lua_origin_root_end",
    "oc_lua_geometry_attributes",
    "oc_lua_geometry_begin",
    "oc_lua_geometry_end",
    "oc_lua_graphics_save",
    "oc_lua_graphics_restore",
   ])
    assert.equal(
     typeof m["_" + n],
     "undefined",
     n + " must remain private native authority",
    );
   if (kind === "lua") {
    assert.equal(m._oc_synctex_abi(), 65536);
    assert.equal(m._oc_synctex_geometry_length(0), 0);
    assert.equal(m._oc_synctex_geometry_length(997), 0);
    assert.equal(m._oc_synctex_geometry_copy(997, 0, 131072), 0);
   } else {
    assert.equal(m._oc_dvi_geometry_size(), 0);
    assert.equal(m._oc_dvi_geometry_length(997), 0);
    assert.equal(m._oc_dvi_geometry_copy(997, 0, 131072), 0);
   }
   rows.push({
    kind,
    generation,
    baseAbi: base,
    graphicsCapability: 1,
    originCapability: generation === "origin1" ? 1 : null,
    privateAuthorityExportsAbsent: true,
    coldGeometryUnavailable: true,
    directory,
    loaderSha256: sha(loader),
    wasmSha256: sha(wasm),
   });
  }
 fs.writeFileSync(
  path.join(out, "receipt.json"),
  JSON.stringify(
   { schemaVersion: 1, status: "pass", rows, skips: 0 },
   null,
   2,
  ) + "\n",
 );
 console.log(
  "all6 old/new tuple/capability/private-authority/cold-copy boundaries pass",
 );
})().catch((e) => {
 console.error(e);
 process.exitCode = 1;
});
