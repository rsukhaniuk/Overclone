"use strict";
// Genuine fresh INITEX, with no seed format. Freeze Date BEFORE module loading
// using the existing ../dvi/fixed-clock.cjs fixture, not output normalization.
const { fs, path, assert, hashBytes, engine } = require("./runtime.cjs");
(async () => {
 const out = process.env.RESULTS;
 assert.ok(out);
 fs.mkdirSync(out, { recursive: true });
 const lines = [],
  x = await engine(lines, false),
  { m, call, map } = x;
 const opened = [],
  original = m.FS.open;
 m.FS.open = function (n, ...a) {
  const s = original.call(this, n, ...a);
  if (typeof n === "string") opened.push(path.posix.resolve(m.FS.cwd(), n));
  return s;
 };
 assert.equal(
  call("oc_mem_configure", ["number", "number", "number"], [1, 1048575, 901]),
  1,
 );
 const rc = call("compileFormat");
 assert.equal(rc, 0);
 assert.equal(map(901).length, 0);
 assert.equal(
  opened.some((n) => n.startsWith("/tex/") && n.endsWith(".fmt")),
  false,
  "INITEX must not load a seed format",
 );
 const b = Buffer.from(m.FS.readFile("/work/xelatex.fmt"));
 assert.ok(b.length > 32 * 1024 * 1024 && b.length < 64 * 1024 * 1024);
 fs.writeFileSync(path.join(out, "xelatex.fmt"), b);
 fs.writeFileSync(
  path.join(out, "format.log"),
  Buffer.from(m.FS.readFile("/work/xelatex.log")),
 );
 for (const n of ["compileLaTeX", "compileBibtex", "compileFormat"])
  assert.equal(call(n), -10);
 assert.equal(map(901).length, 0);
 const receipt = {
  schemaVersion: 1,
  status: "pass",
  nativeReturn: rc,
  identities: x.identities,
  iniSha256: hashBytes(fs.readFileSync("/inputs/suite/files/xelatex.ini")),
  format: { length: b.length, sha256: hashBytes(b) },
  actualOpened: opened,
  seedFormatLoaded: false,
  repeatNativeDenied: true,
  rawMapBytes: 0,
  clock:
   "existing fixed-clock.cjs injected before Emscripten initialization; TZ=UTC",
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
