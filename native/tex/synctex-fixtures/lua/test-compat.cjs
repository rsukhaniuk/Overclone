// Exact ABI3/ABI4/ABI5 no-capability output regression; synthetic fixture.
"use strict";
const { load, sha, fs, path, assert } = require("./runtime.cjs");
(async () => {
  const abi = Number(process.env.LUA_ABI || 4);
  let checks = 0;
  const out = process.env.RESULTS || "/control/r2-lua-maps/evidence/compat-new";
  fs.mkdirSync(out, { recursive: true });
  const root = "розділ/my thesis.tex",
    lines = [];
  const { m, call, seed, identities } = await load(root, lines, abi);
  // Explicit source-level suppression makes exact comparisons deterministic.
  // We do not normalize/rewrite the resulting PDF or mask any output bytes.
  const source = Buffer.from(String.raw`\documentclass{article}
\pdfvariable suppressoptionalinfo=767
\pdfvariable compresslevel=0
\pdfvariable objcompresslevel=0
\begin{document}
Same native no-map bytes. \input{"глави/chapter one.tex"}
\end{document}
`);
  seed(1, root, source);
  seed(1, "глави/chapter one.tex", Buffer.from("INCLUDE-MARK\n"));
  assert.equal(call("oc_luatex_compile_latex"), 0);
  ++checks;
  const outputs = {};
  for (let i = 0; i < 10; ++i) {
    const n = call("oc_luatex_output_size", ["number"], [i]);
    assert.ok(n >= 0);
    ++checks;
    let bytes = Buffer.alloc(0);
    if (n) {
      const p = m._malloc(n);
      assert.equal(
        call(
          "oc_luatex_copy_output",
          ["number", "number", "number"],
          [i, p, n],
        ),
        n,
      );
      ++checks;
      bytes = Buffer.from(m.HEAPU8.subarray(p, p + n));
      m._free(p);
    }
    fs.writeFileSync(path.join(out, "artifact-" + i), bytes);
    outputs[i] = { length: n, sha256: sha(bytes) };
  }
  if (abi === 4 || abi === 5) {
    assert.equal(m._oc_synctex_length(401), 0);
    assert.equal(m._oc_synctex_geometry_length(401), 0);
    checks += 2;
  }
  if (process.env.COMPARE_TO) {
    for (let i = 0; i < 10; ++i) {
      assert.deepEqual(
        fs.readFileSync(path.join(out, "artifact-" + i)),
        fs.readFileSync(path.join(process.env.COMPARE_TO, "artifact-" + i)),
        "exact indexed output " + i,
      );
      ++checks;
    }
  }
  fs.writeFileSync(path.join(out, "console.txt"), lines.join("\n"));
  const result = {
    schemaVersion: 1,
    status: "pass",
    checks,
    skips: 0,
    abi,
    identities,
    sourceSha256: sha(source),
    outputs,
    comparison: process.env.COMPARE_TO || null,
  };
  fs.writeFileSync(
    path.join(out, "receipt.json"),
    JSON.stringify(result, null, 2) + "\n",
  );
  console.log(JSON.stringify(result));
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
