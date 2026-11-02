"use strict";
// Genuine candidate calls; these are native fixtures, not browser acceptance.
const { load, sha, fs, path, assert } = require("./runtime.cjs");
const mode = process.argv[2] || "controls";
const output = process.env.RESULTS;
assert.ok(output, "explicit ABI5 evidence directory required");
const root = "розділ/geometry thesis.tex",
  tag = 501;
(async () => {
  fs.mkdirSync(output, { recursive: true });
  const lines = [],
    { m, call, seed, read, identities } = await load(root, lines, 5);
  let checks = 0;
  const eq = (a, b) => {
    assert.deepEqual(a, b);
    ++checks;
  };
  eq(call("oc_synctex_geometry_length", ["number"], [tag]), 0);
  eq(
    call("oc_mem_configure", ["number", "number", "number"], [1, 1048575, tag]),
    1,
  );
  for (const name of [
    "oc_lua_geometry_begin",
    "oc_lua_geometry_end",
    "oc_lua_geometry_unsafe",
    "oc_lua_geometry_attributes",
    "oc_lua_geometry_observe",
  ])
    eq(typeof m["_" + name], "undefined");
  let text = fs.readFileSync(path.join(__dirname, "geometry", root), "utf8");
  const mutations = {
    literal: String.raw`\pdfextension literal direct {1 0 0 1 11 13 cm}`,
    "lua-print": String.raw`\latelua{pdf.print("page", "1 0 0 1 11 13 cm")}`,
    "lua-literal": String.raw`\directlua{local n=node.new("whatsit","pdf_literal"); n.mode=1; n.data="1 0 0 1 11 13 cm"; node.write(n)}`,
    setmatrix: String.raw`\pdfextension setmatrix{2 0 0 2}`,
    colorstack: String.raw`\pdfextension colorstack 0 set {1 0 0 1 11 13 cm}`,
    "changed-height": String.raw`\latelua{tex.pageheight=tex.pageheight+65536}`,
  };
  if (mutations[mode])
    text = text.replace(
      "\\kern20bp\\hbox{",
      mutations[mode] + "\r\n\\kern20bp\\hbox{",
    );
  const attributeControls = {
    "rotate-prefix": "/Rotate90",
    "rotate-signed-prefix": "/Rotate+90",
    "crop-prefix": "/CropBoxExtra [5 10 290 190]",
    "name-escape": "/Rot#61te 90",
    "numeric-junk": "/Rotate 90junk",
    "attribute-duplicate": "/Rotate 0 /Rotate 90",
    "valid-delimiters": "/CropBox[5 10 290 190]/Rotate 0",
  };
  if (attributeControls[mode])
    text = text.replace(
      "/CropBox [5 10 290 190] /Rotate 0",
      attributeControls[mode],
    );
  if (mode === "attributes")
    text = text.replace("/CropBox [5 10 290 190] /Rotate 0", "/Contents 1 0 R");
  if (mode === "duplicate-media")
    text = text.replace(
      "/CropBox [5 10 290 190] /Rotate 0",
      "/MediaBox [-20 -30 280 170]",
    );
  if (mode === "lua-attributes")
    text = text.replace(
      "/CropBox [-20 -30 345 230] /Rotate 90",
      "/Contents 1 0 R",
    );
  if (mode === "tree-attributes")
    text = text.replace(
      "\\begin{document}",
      String.raw`\begin{document}\directlua{pdf.setpagesattributes("/Kids []")}`,
    );
  if (mode === "reorder")
    text = text.replace(
      "\\begin{document}",
      String.raw`\begin{document}\directlua{luatexbase.add_to_callback("page_order_index",function(n) return 0 end,"r2-reorder")}`,
    );
  if (mode === "raw-object")
    text = text.replace(
      "\\begin{document}",
      String.raw`\begin{document}\directlua{pdf.immediateobj("null")}`,
    );
  if (mode === "tex-object")
    text = text.replace(
      "\\begin{document}",
      String.raw`\begin{document}\immediate\pdfextension obj{null}`,
    );
  if (mode === "fallback")
    text = text.replace(
      "\\pagewidth=300bp \\pageheight=200bp",
      "\\pagewidth=0bp \\pageheight=0bp",
    );
  if (mode === "direction")
    text = text.replace(
      "\\shipout\\vbox to 150bp",
      "\\pagedir TRT \\bodydir TRT \\shipout\\vbox to 150bp",
    );
  if (mode === "form")
    text = text.replace(
      "\\begin{document}",
      String.raw`\begin{document}\setbox0=\hbox{FORMMARK}\saveboxresource0`,
    );
  if (mode === "form")
    text = text.replace(
      "ROOTMARK",
      String.raw`ROOTMARK\useboxresource\lastsavedboxresourceindex`,
    );
  if (mode === "pages512" || mode === "pages513") {
    text =
      text.slice(0, text.indexOf("\\pagewidth=300bp")) +
      String.raw`\pagewidth=300bp\pageheight=200bp
\newcount\fixturecount\fixturecount=0
\loop\advance\fixturecount by 1
\shipout\vbox{\hbox{MANYMARK}}
\ifnum\fixturecount<` +
      (mode === "pages512" ? "512" : "513") +
      String.raw`\repeat
\end{document}`;
  }
  const source = Buffer.from(text);
  eq(source.subarray(0, 3), Buffer.from([0xef, 0xbb, 0xbf]));
  assert.ok(text.includes("😀") && text.includes("е́"));
  ++checks;
  fs.writeFileSync(path.join(output, mode + ".source.tex"), source);
  const inputs = {};
  for (const rel of [root, "стилі/local style.sty", "глави/chapter one.tex"]) {
    const b =
      rel === root
        ? source
        : fs.readFileSync(path.join(__dirname, "project", rel));
    seed(1, rel, b);
    inputs[rel] = { length: b.length, sha256: sha(b) };
  }
  const status = call("oc_luatex_compile_latex");
  fs.writeFileSync(path.join(output, mode + ".console.txt"), lines.join("\n"));
  eq(status, 0);
  eq(Buffer.from(m.FS.readFile("/work/project/" + root)), source);
  const n = call("oc_luatex_output_size", ["number"], [0]);
  assert.ok(n > 0);
  ++checks;
  const ptr = m._malloc(n);
  eq(
    call("oc_luatex_copy_output", ["number", "number", "number"], [0, ptr, n]),
    n,
  );
  const pdf = Buffer.from(m.HEAPU8.subarray(ptr, ptr + n));
  m._free(ptr);
  const raw = read("oc_synctex_length", "oc_synctex_copy", tag);
  const geometry = read(
    "oc_synctex_geometry_length",
    "oc_synctex_geometry_copy",
    tag,
    131072,
  );
  assert.ok(raw.length);
  ++checks;
  let g = null;
  if (mode === "pages513") eq(geometry.length, 0);
  else {
    try {
      g = JSON.parse(geometry);
    } catch (error) {
      throw new Error("invalid geometry JSON", { cause: error });
    }
    eq(g.schemaVersion, 1);
    eq(g.pages.length, mode === "pages512" ? 512 : 4);
    const allUnsafe =
      (Object.hasOwn(attributeControls, mode) && mode !== "valid-delimiters") ||
      [
        "literal",
        "lua-print",
        "lua-literal",
        "setmatrix",
        "colorstack",
        "attributes",
        "duplicate-media",
        "lua-attributes",
        "tree-attributes",
        "reorder",
        "raw-object",
        "tex-object",
        "form",
      ].includes(mode);
    for (const [i, p] of g.pages.entries()) {
      eq(p.page, i + 1);
      const supported =
        !allUnsafe &&
        !(["direction", "changed-height"].includes(mode) && i === 0) &&
        mode !== "direction";
      if (supported) {
        eq(p.status, "available");
        eq(p.reason, "none");
        eq(p.writerToPdfRaw.length, 6);
        eq(p.writerPageBoxBp.length, 4);
        assert.ok(
          [...p.writerToPdfRaw, ...p.writerPageBoxBp].every(Number.isFinite),
        );
        ++checks;
      } else
        eq(p, {
          page: i + 1,
          status: "unsupported",
          reason: "out-of-domain",
          writerToPdfRaw: null,
          writerPageBoxBp: null,
        });
    }
  }
  for (const [ext, b] of [
    ["pdf", pdf],
    ["synctex", raw],
    ["geometry.json", geometry],
  ])
    fs.writeFileSync(path.join(output, mode + "." + ext), b);
  for (const t of [0, tag - 1, tag + 1])
    eq(call("oc_synctex_geometry_length", ["number"], [t]), 0);
  if (geometry.length) {
    const p = m._malloc(geometry.length);
    m.HEAPU8.fill(0x5a, p, p + geometry.length);
    for (const [token, cap] of [
      [tag, geometry.length - 1],
      [tag + 1, geometry.length],
      [tag, 131073],
    ]) {
      eq(
        call(
          "oc_synctex_geometry_copy",
          ["number", "number", "number"],
          [token, p, cap],
        ),
        0,
      );
      eq(
        m.HEAPU8.subarray(p, p + geometry.length).every((x) => x === 0x5a),
        true,
      );
    }
    m._free(p);
  }
  eq(call("oc_luatex_compile_latex"), -2);
  eq(call("oc_synctex_geometry_length", ["number"], [tag]), 0);
  const receipt = {
    schemaVersion: 1,
    status: "pass",
    mode,
    checks,
    skips: 0,
    identities,
    inputs,
    outputs: Object.fromEntries(
      [
        ["pdf", pdf],
        ["map", raw],
        ["geometry", geometry],
      ].map(([k, b]) => [k, { length: b.length, sha256: sha(b) }]),
    ),
  };
  fs.writeFileSync(
    path.join(output, mode + ".json"),
    JSON.stringify(receipt, null, 2) + "\n",
  );
  console.log(JSON.stringify(receipt));
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
