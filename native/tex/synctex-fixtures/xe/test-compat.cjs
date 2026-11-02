"use strict";
// Complete native output bytes under an explicit fixed clock. No filtering or
// source rewriting. A different required format is recorded, never normalized.
const { fs, path, assert, hashBytes, engine } = require("./runtime.cjs");
(async () => {
 const out = process.env.RESULTS;
 assert.ok(out);
 fs.mkdirSync(out, { recursive: true });
 const reference = process.env.REFERENCE === "1",
  maps = process.env.MAPS || "1";
 let m,
  call,
  seed,
  map,
  identities,
  lines = [];
 if (reference) {
  const dir = process.env.REFERENCE_ENGINE || "/inputs/suite/files";
  const wasm = fs.readFileSync(path.join(dir, "xetex.wasm"));
  m = await require(path.join(dir, "xetex.js"))({
   wasmBinary: wasm,
   print: (s) => lines.push(s),
   printErr: (s) => lines.push(s),
  });
  call = (n, t = [], a = []) => m.ccall(n, "number", t, a);
  m.FS.mkdir("/work");
  m.FS.mkdir("/tex");
  m.FS.chdir("/work");
  seed = (n, b) => {
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
  for (const p of pins) {
   if (/\.(wasm|js|fmt)$/.test(p.name) || p.name.startsWith("notices/"))
    continue;
   const b = fs.readFileSync("/inputs/suite/files/" + p.name);
   assert.equal(b.length, p.length);
   assert.equal(hashBytes(b), p.sha256);
   seed("/tex/" + p.name, b);
  }
  const fmt = fs.readFileSync("/inputs/suite/files/xelatex.fmt");
  seed("/tex/xelatex.fmt", fmt);
  map = () => Buffer.alloc(0);
  identities = {
   wasm: hashBytes(wasm),
   loader: hashBytes(fs.readFileSync(path.join(dir, "xetex.js"))),
   format: hashBytes(fmt),
   suitePins: hashBytes(pinsBytes),
  };
 } else {
  const x = await engine(lines);
  ({ m, call, seed, map, identities } = x);
  if (maps !== "default")
   assert.equal(
    call(
     "oc_mem_configure",
     ["number", "number", "number"],
     [maps === "1" ? 1 : 0, maps === "1" ? 1048575 : 0, 801],
    ),
    1,
   );
 }
 const root = "розділ/my thesis.tex",
  project = path.join(__dirname, "project"),
  files = {};
 function copy(dir) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
   const p = path.join(dir, e.name);
   if (e.isDirectory()) copy(p);
   else {
    const b = fs.readFileSync(p),
     rel = path.relative(project, p);
    seed("/work/" + rel, b);
    files[rel] = { length: b.length, sha256: hashBytes(b) };
   }
  }
 }
 copy(project);
 assert.equal(call("setMainEntry", ["string"], [root]), 0);
 assert.equal(call("compileLaTeX"), 0);
 const outputs = {},
  differences = [];
 for (const ext of ["xdv", "log", "aux"]) {
  const b = Buffer.from(m.FS.readFile("/work/my thesis." + ext));
  fs.writeFileSync(path.join(out, "main." + ext), b);
  outputs[ext] = { length: b.length, sha256: hashBytes(b) };
  if (process.env.COMPARE_TO) {
   const old = fs.readFileSync(
    path.join(process.env.COMPARE_TO, "main." + ext),
   );
   if (!b.equals(old))
    differences.push({
     ext,
     old: { length: old.length, sha256: hashBytes(old) },
     current: outputs[ext],
    });
  }
 }
 const raw = map(801);
 fs.writeFileSync(path.join(out, "map.synctex"), raw);
 if (!reference && maps === "1") assert.ok(raw.length);
 else assert.equal(raw.length, 0);
 if (process.env.COMPARE_TO && !process.env.RECORD_ABI_DIFFERENCE)
  assert.deepEqual(differences, [], "complete native output bytes");
 for (const [n, h] of Object.entries(files))
  assert.equal(hashBytes(Buffer.from(m.FS.readFile("/work/" + n))), h.sha256);
 const receipt = {
  schemaVersion: 1,
  status: "pass",
  reference,
  maps,
  files,
  identities,
  outputs,
  rawMap: { length: raw.length, sha256: hashBytes(raw) },
  differences,
  comparison: process.env.COMPARE_TO || null,
  comparisonRequiredEqual: !process.env.RECORD_ABI_DIFFERENCE,
  clock:
   "fixed-clock.cjs before Emscripten initialization; monotonic timing real; TZ=UTC; complete bytes, no normalization",
 };
 fs.writeFileSync(
  path.join(out, "receipt.json"),
  JSON.stringify(receipt, null, 2) + "\n",
 );
 console.log(JSON.stringify(receipt));
})().catch((e) => {
 console.error(e);
 process.exitCode = 1;
});
