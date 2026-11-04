"use strict";
// Native fixture tooling only. Reuse the exact accepted native FFI runner;
// override only its input project and add explicit capability assertions.
const fs = require("node:fs"),
 path = require("node:path"),
 assert = require("node:assert/strict"),
 crypto = require("node:crypto"),
 Module = require("node:module");
const parent = path.join(__dirname, "../graphics-navigation/reproduce.cjs");
let text = fs.readFileSync(parent, "utf8");
assert.equal(
 crypto.createHash("sha256").update(text).digest("hex"),
 "5df2186805af3d5cef745f4ae0da0f94544271b797de87777515680b78c5d6db",
);
function replace(x, y) {
 assert.equal(text.split(x).length, 2, "exact accepted fixture seam");
 text = text.replace(x, y);
}
replace(
 "const names=kind===",
 `if(process.env.PROJECT){
 for(const n of Object.keys(project))delete project[n];
 const base=path.resolve(process.env.PROJECT);
 function walk(d){for(const ent of fs.readdirSync(d,{withFileTypes:true})){
  const p=path.join(d,ent.name);assert.ok(!ent.isSymbolicLink());
  if(ent.isDirectory())walk(p);else {assert.ok(ent.isFile());project[path.relative(base,p).split(path.sep).join('/')]=fs.readFileSync(p);}
 }}walk(base);assert.ok(project[root]);
}
const names=kind===`,
);
replace(
 "assert.equal(instantiated,1);",
 `assert.equal(instantiated,1);
 if(process.env.REQUIRE_ORIGIN==='1'&&(operation==='convert'||(kind==='lua'&&operation==='latex'))){
  assert.equal(kind==='lua'?m._oc_lua_graphics_abi():m._oc_dvi_graphics_abi(),1);
  assert.equal(kind==='lua'?m._oc_lua_origin_abi():m._oc_dvi_origin_abi(),1);
 }`,
);
const child = new Module(parent, module);
child.filename = parent;
child.paths = Module._nodeModulePaths(path.dirname(parent));
child._compile(text, parent);
