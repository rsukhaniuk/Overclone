"use strict";
// Native test tooling: original project bytes are never rewritten by the engine.
const {fs, path, assert, hashBytes, engine} = require("../../synctex-fixtures/xe/runtime.cjs");
const {parentPort} = require("node:worker_threads");
const mode = process.argv[2] || "positive";
const out = process.env.RESULTS;
assert.ok(out, "RESULTS is required");
const root = mode === "output-collision" ? "same.xdv" :
  process.env.ROOT_NAME || (mode === "bmp" ? "тези/розділ.tex" : "тези/розділ 😀.tex");
const include = mode === "bmp" ? "глави/частина.tex" : "глави/частина 🧪.tex";
const tag = 1201;
const base = path.posix.basename(root, ".tex");
const positive = !["failure", "read-traversal", "overlong", "output-collision"].includes(mode);
const ownFont = mode === "font";
(async () => {
  fs.mkdirSync(out, {recursive: true});
  const lines = [], x = await engine(lines), {m, call, seed, map} = x;
  if (mode === "baseline-rejection") {
    assert.equal(typeof m._oc_xe_nonbmp_abi, "undefined");
    assert.equal(call("setMainEntry", ["string"], [root]), -2);
    fs.writeFileSync(path.join(out, mode + ".json"), JSON.stringify({status: "pass", root,
      nativeReturn: -2, scope: "unchanged accepted engine refuses original non-BMP root", identities: x.identities}, null, 2));
    return;
  }
  if (process.env.BASELINE_BMP === "1") assert.equal(mode, "bmp");
  else assert.equal(call("oc_xe_nonbmp_abi"), 1);
  if (mode === "invalid-main" || mode === "invalid-utf8") {
    let value;
    if (mode === "invalid-main") value = process.env.INVALID_MAIN || "../secret.tex";
    else value = Buffer.from(process.env.INVALID_UTF8_HEX || "78eda0802e746578", "hex");
    let rc;
    if (Buffer.isBuffer(value)) {
      const p = m._malloc(value.length + 1);
      m.HEAPU8.set(value, p); m.HEAPU8[p + value.length] = 0;
      rc = call("setMainEntry", ["number"], [p]); m._free(p);
    } else rc = call("setMainEntry", ["string"], [value]);
    assert.equal(rc, -2);
    assert.equal(call("compileLaTeX"), -1);
    assert.equal(map(tag).length, 0);
    fs.writeFileSync(path.join(out, mode + ".json"), JSON.stringify({status: "pass", mode,
      value: Buffer.isBuffer(value) ? value.toString("hex") : value, nativeReturn: rc, identities: x.identities}, null, 2));
    return;
  }
  let source = "\ufeff% Labelled synthetic Unicode-path fixture; Україна 😀\r\n" + String.raw`\documentclass{article}
\usepackage{fontspec}
` + (ownFont ? String.raw`\setmainfont{DejaVu 😀.ttf}[Path=шрифти/,BoldFont={DejaVu Bold 😀.ttf},ItalicFont={DejaVu Italic 😀.ttf},BoldItalicFont={DejaVu BoldItalic 😀.ttf}]
` : String.raw`\setmainfont{DejaVuSerif.ttf}[Path=/tex/,BoldFont=DejaVuSerif-Bold.ttf,ItalicFont=DejaVuSerif-Italic.ttf,BoldItalicFont=DejaVuSerif-BoldItalic.ttf]
`) + String.raw`\begin{document}
ROOTMARK Український текст.
\input{\detokenize{глави/частина 🧪.tex}}
Reference: \ref{sec:included}.
\end{document}
`;
  source = source.replace("глави/частина 🧪.tex", include);
  if (mode === "failure") source = source.replace("ROOTMARK", "\\UNDEFINEDNONBMP");
  if (mode === "read-traversal") {
    seed("/secret.tex", Buffer.from("\\errmessage{SECRET-READ}"));
    source = source.replace("ROOTMARK", "\\input{../secret.tex} ROOTMARK");
  }
  if (mode === "runaway") source = source.replace("\\end{document}",
    String.raw`\immediate\write16{RUNAWAY-BEGIN}\loop\iftrue\repeat`);
  if (["bib-pass1", "bib-pass2", "bib-pass3"].includes(mode))
    source = source.replace("\\end{document}", String.raw`Citation: \cite{fixture}.
\bibliographystyle{plain}
\bibliography{references}
\end{document}`);
  const files = new Map([
    [root, Buffer.from(source, "utf8")],
    [include, Buffer.from("\\section{INCLUDEMARK}\\label{sec:included}\nВключений текст.\n", "utf8")],
  ]);
  for (const [name, bytes] of files) seed("/work/" + name, bytes);
  if (ownFont) {
    for (const [original, alias] of [["DejaVuSerif.ttf", "DejaVu 😀.ttf"],
      ["DejaVuSerif-Bold.ttf", "DejaVu Bold 😀.ttf"],
      ["DejaVuSerif-Italic.ttf", "DejaVu Italic 😀.ttf"],
      ["DejaVuSerif-BoldItalic.ttf", "DejaVu BoldItalic 😀.ttf"]]) {
      const font = fs.readFileSync("/inputs/suite/files/" + original);
      files.set("шрифти/" + alias, font); seed("/work/шрифти/" + alias, font);
    }
  }
  if (mode === "overlong") seed("/work/oversized.bin", new Uint8Array(8388609));
  if (mode === "collision") seed("/work/" + base + ".xdv", Buffer.from([247, 7, 9, 9]));
  if (["bib-pass1", "bib-pass2", "bib-pass3", "bibtex"].includes(mode))
    seed("/work/references.bib", Buffer.from("@book{fixture,title={BIBMARK},author={Author, A},publisher={Fixture Press},year={2025}}\n"));
  if (mode === "bib-pass2" || mode === "bib-pass3") {
    const prior = mode === "bib-pass2" ? "bib-pass1.aux" : "bib-pass2.aux";
    seed("/work/" + base + ".aux", fs.readFileSync(path.join(out, prior)));
    seed("/work/" + base + ".bbl", fs.readFileSync(path.join(out, "bibtex.bbl")));
  }
  if (mode === "bibtex") {
    // The existing native backend consumes the selected basename in a separate
    // fresh module. This is an engine-produced AUX, never a simulated BBL.
    seed("/work/" + base + ".aux", fs.readFileSync(path.join(out, "bib-pass1.aux")));
    assert.equal(call("setMainEntry", ["string"], [base + ".tex"]), 0);
    const rc = call("compileBibtex");
    fs.writeFileSync(path.join(out, mode + ".console.txt"), lines.join("\n"));
    assert.equal(rc, 0, lines.slice(-15).join("\n"));
    const bbl = Buffer.from(m.FS.readFile("/work/" + base + ".bbl"));
    assert.ok(bbl.includes(Buffer.from("BIBMARK")));
    fs.writeFileSync(path.join(out, "bibtex.bbl"), bbl);
    fs.writeFileSync(path.join(out, mode + ".json"), JSON.stringify({status: "pass", root,
      nativeReturn: rc, bbl: {length: bbl.length, sha256: hashBytes(bbl)}, identities: x.identities}, null, 2));
    return;
  }
  assert.equal(call("oc_mem_configure", ["number", "number", "number"], [1, 1048575, tag]), 1);
  assert.equal(call("setMainEntry", ["string"], [root]), 0);
  const opened = [], originalOpen = m.FS.open;
  m.FS.open = function (name, ...args) {
    const result = originalOpen.call(this, name, ...args);
    if (typeof name === "string") opened.push(path.posix.resolve(m.FS.cwd(), name));
    return result;
  };
  const nativeReturn = call("compileLaTeX");
  fs.writeFileSync(path.join(out, mode + ".console.txt"), lines.join("\n"));
  if (mode === "runaway") throw new Error("A runaway compile must be physically terminated");
  if (positive) {
    assert.equal(nativeReturn, 0, lines.slice(-20).join("\n"));
    assert.ok(opened.includes("/work/" + root));
    assert.ok(opened.includes("/work/" + include));
    if (ownFont) assert.ok(opened.includes("/work/шрифти/DejaVu 😀.ttf"));
    const output = Buffer.from(m.FS.readFile("/work/" + base + ".xdv"));
    assert.equal(output[0], 247); assert.equal(output[1], 7);
    assert.ok(output.length > 100);
    fs.writeFileSync(path.join(out, mode + ".xdv"), output);
    const rawMap = map(tag);
    assert.ok(rawMap.includes(Buffer.from(root)), "raw native map must retain original root UTF-8");
    assert.ok(rawMap.includes(Buffer.from(include)), "raw native map must retain original include UTF-8");
    fs.writeFileSync(path.join(out, mode + ".synctex"), rawMap);
    if (["bib-pass1", "bib-pass2", "bib-pass3"].includes(mode))
      fs.writeFileSync(path.join(out, mode + ".aux"), Buffer.from(m.FS.readFile("/work/" + base + ".aux")));
    if (mode === "bib-pass3") assert.ok(!lines.some(line => /Citation .* undefined/.test(line)));
  } else {
    assert.notEqual(nativeReturn, 0);
    if (mode === "output-collision") assert.equal(nativeReturn, -5);
    assert.equal(map(tag).length, 0);
    if (mode === "failure") assert.ok(lines.some(line => line.includes(root) && /:[0-9]+:/.test(line)),
      "native diagnostic must retain original filename and line");
    if (mode === "read-traversal") {
      assert.ok(!opened.includes("/secret.tex"));
      assert.ok(!lines.some(line => line.includes("SECRET-READ")));
    }
  }
  for (const [name, bytes] of files) assert.deepEqual(Buffer.from(m.FS.readFile("/work/" + name)), bytes);
  const receipt = {schemaVersion: 1, status: "pass", mode, nativeReturn, root,
    originalSourcePreserved: true, originalFiles: Object.fromEntries([...files].map(([name, bytes]) =>
      [name, {length: bytes.length, sha256: hashBytes(bytes)}])), actualOpened: opened,
    identities: x.identities, rawMapBytes: map(tag).length};
  fs.writeFileSync(path.join(out, mode + ".json"), JSON.stringify(receipt, null, 2));
  console.log(JSON.stringify({mode, nativeReturn, rawMapBytes: receipt.rawMapBytes}));
})().catch(error => { console.error(error); parentPort?.postMessage({error: String(error)}); process.exitCode = 1; });
