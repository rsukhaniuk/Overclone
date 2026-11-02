"use strict";
const { load, sha, fs, path, assert } = require("./runtime.cjs");
const mode = process.argv[2] || "positive";
const output = process.env.RESULTS || "/control/r2-lua-maps/evidence/runtime";
const root = "розділ/my thesis.tex",
  tag = Number(process.env.PASS_TAG || 401);
const project = path.join(__dirname, "project");
(async () => {
  fs.mkdirSync(output, { recursive: true });
  const lines = [];
  const { m, call, seed, read, identities } = await load(root, lines);
  let checks = 0;
  const eq = (a, b, msg) => {
    assert.equal(a, b, msg);
    ++checks;
  };
  const maplen = (t) => call("oc_synctex_length", ["number"], [t]);
  const geolen = (t) => call("oc_synctex_geometry_length", ["number"], [t]);
  const config = (mode, cap, t = tag) =>
    call("oc_mem_configure", ["number", "number", "number"], [mode, cap, t]);
  eq(maplen(tag), 0);
  eq(geolen(tag), 0);
  for (const name of [
    "oc_synctex_finish",
    "oc_mem_stage_checked",
    "oc_synctex_pdf_close",
    "oc_lua_maps_ship_page",
  ])
    eq(
      typeof m["_" + name],
      "undefined",
      "no exported caller-controlled seal/hook",
    );
  if (!["default-off", "carry-off"].includes(mode))
    eq(
      config(
        mode === "off" ? 0 : 1,
        mode === "off"
          ? 0
          : mode === "cap"
            ? 1
            : Number(process.env.MAP_CAP || 1048575),
      ),
      1,
    );
  if (mode === "invalid-config") eq(config(2, 100), 0);
  if (mode === "repeat-config") eq(config(1, 1048575), 0);
  let source = fs.readFileSync(path.join(project, root));
  const text = source.toString("utf8");
  if (mode === "off" || mode === "default-off")
    source = Buffer.from(
      text.replace("\\begin{document}", "\\synctex=1\n\\begin{document}"),
    );
  if (mode === "native-fail")
    source = Buffer.from(
      text.replace(
        "\\shipout\\vbox to 200bp{\\offinterlineskip\r\n\\kern60bp",
        "\\errmessage{INTENTIONAL-AFTER-SHIPOUT}\n\\shipout\\vbox to 200bp{\\offinterlineskip\r\n\\kern60bp",
      ),
    );
  if (mode === "pdf-fail")
    source = Buffer.from(
      text.replace(
        "\\begin{document}",
        '\\directlua{luatexbase.add_to_callback("finish_pdffile", function() error("INTENTIONAL-PDF-FINALIZE") end, "r2-pdf-fail")}\n\\begin{document}',
      ),
    );
  if (mode === "quota")
    source = Buffer.from(
      text.replace(
        "\\end{document}",
        '\\immediate\\write16{SHIPOUT-WITNESS}\\directlua{local f=assert(io.open("/work/output/flood.aux","wb")); f:write(string.rep("X",8388609)); f:close()}\n\\end{document}',
      ),
    );
  if (mode === "changed")
    source = Buffer.from(
      text.replace("ROOTMARK", "ROOTSHIFT").replace("\\kern40bp", "\\kern45bp"),
    );
  if (mode === "unicode")
    source = Buffer.from(
      text
        .replace(
          "\\documentclass{article}",
          String.raw`\documentclass{article}
\usepackage{fontspec}
\setmainfont{DejaVuSerif.ttf}[Path=/work/runtime/]`,
        )
        .replace("ROOTMARK", "Україна ROOTMARK")
        .replace(
          "\\begin{document}",
          String.raw`\begin{document}
\directlua{texio.write_nl("EMOJI-" .. utf8.codepoint("😀") .. string.char(10))}`,
        ),
    );
  if (mode === "postamble") {
    const golden = fs.readFileSync(path.join(output, "positive.synctex"));
    const cap = Number(process.env.MAP_CAP);
    assert.ok(
      cap > golden.indexOf("Postamble:") && cap < golden.length,
      "overflow must occur in the actual postamble",
    );
    ++checks;
  }
  if (mode === "runaway")
    source = Buffer.from(
      text.replace(
        "\\end{document}",
        '\\directlua{texio.write_nl("RUNAWAY-BEGIN" .. string.char(10)); while true do end}\n\\end{document}',
      ),
    );
  fs.writeFileSync(path.join(output, mode + ".source.tex"), source);
  const files = {};
  for (const rel of [root, "стилі/local style.sty", "глави/chapter one.tex"]) {
    const bytes =
      rel === root ? source : fs.readFileSync(path.join(project, rel));
    files[rel] = { length: bytes.length, sha256: sha(bytes) };
    seed(1, rel, bytes);
  }
  if (["carry-off", "carry-on", "forbidden-carry"].includes(mode)) {
    seed(2, "result.aux", fs.readFileSync(path.join(output, "positive.aux")));
    // Previously produced bytes are not an admitted AUX carry type.
    const old = fs.readFileSync(path.join(output, "positive.synctex"));
    const p = m._malloc(old.length);
    m.HEAPU8.set(old, p);
    if (mode === "forbidden-carry")
      eq(
        call(
          "oc_luatex_add_file",
          ["number", "string", "number", "number"],
          [2, "result.synctex", p, old.length],
        ),
        -1,
      );
    m._free(p);
  }
  const status = call("oc_luatex_compile_latex");
  fs.writeFileSync(path.join(output, mode + ".console.txt"), lines.join("\n"));
  eq(
    Buffer.from(m.FS.readFile("/work/project/" + root)).equals(source),
    true,
    "selected exact source bytes",
  );
  if (mode === "unicode") {
    assert.ok(
      lines.some((line) => line.includes("EMOJI-128512")),
      "actual Lua codepoint from original emoji source",
    );
    ++checks;
  }
  const budgetText = call("oc_luatex_budget_json", [], [], "string");
  let budget;
  try {
    budget = JSON.parse(budgetText);
  } catch (error) {
    throw new Error("invalid native budget JSON", { cause: error });
  }
  let raw = Buffer.alloc(0),
    geometry = Buffer.alloc(0),
    pdf = Buffer.alloc(0);
  const pdfSize = call("oc_luatex_output_size", ["number"], [0]);
  if (pdfSize > 0) {
    const p = m._malloc(pdfSize);
    eq(
      call(
        "oc_luatex_copy_output",
        ["number", "number", "number"],
        [0, p, pdfSize],
      ),
      pdfSize,
    );
    pdf = Buffer.from(m.HEAPU8.subarray(p, p + pdfSize));
    m._free(p);
    fs.writeFileSync(path.join(output, mode + ".pdf"), pdf);
  }
  if (status === 0) {
    const n = call("oc_luatex_output_size", ["number"], [3]);
    if (n > 0) {
      const p = m._malloc(n);
      eq(
        call(
          "oc_luatex_copy_output",
          ["number", "number", "number"],
          [3, p, n],
        ),
        n,
      );
      fs.writeFileSync(
        path.join(output, mode + ".aux"),
        Buffer.from(m.HEAPU8.subarray(p, p + n)),
      );
      m._free(p);
    }
  }
  if (["native-fail", "pdf-fail", "quota"].includes(mode)) {
    assert.notEqual(status, 0);
    ++checks;
    eq(pdfSize, 0);
    eq(maplen(tag), 0);
    eq(geolen(tag), 0);
    assert.ok(
      lines.some((x) => x.includes("[1")),
      "must have actual shipout before failure",
    );
    ++checks;
  } else {
    eq(status, 0, call("oc_luatex_error", [], [], "string"));
    assert.ok(pdfSize > 0);
    ++checks;
    if (
      [
        "off",
        "default-off",
        "carry-off",
        "forbidden-carry",
        "cap",
        "postamble",
        "invalid-config",
        "repeat-config",
      ].includes(mode)
    ) {
      eq(maplen(tag), 0);
      eq(geolen(tag), 0);
      eq(
        call("oc_synctex_status"),
        ["off", "default-off", "carry-off"].includes(mode) ? 0 : 2,
      );
    } else {
      eq(call("oc_synctex_status"), 1);
      eq(maplen(0), 0);
      eq(maplen(tag + 1), 0);
      eq(geolen(tag + 1), 0);
      if (mode === "carry-on") {
        eq(maplen(401), 0);
        eq(geolen(401), 0);
      }
      raw = read("oc_synctex_length", "oc_synctex_copy", tag);
      geometry = read(
        "oc_synctex_geometry_length",
        "oc_synctex_geometry_copy",
        tag,
        131072,
      );
      assert.ok(raw.toString().startsWith("SyncTeX Version:1\n"));
      ++checks;
      assert.match(
        raw.toString(),
        /Output:pdf\nMagnification:1000\nUnit:1\nX Offset:0\nY Offset:0\n/,
      );
      ++checks;
      assert.match(
        raw.toString(),
        /Postamble:\nCount:\d+\n!\d+\nPost scriptum:\n$/,
      );
      ++checks;
      for (const rel of Object.keys(files)) {
        assert.ok(
          raw
            .toString()
            .split("\n")
            .some((s) => /^Input:\d+:/.test(s) && s.endsWith("/" + rel)),
          rel,
        );
        ++checks;
      }
      let g;
      try {
        g = JSON.parse(geometry);
      } catch (error) {
        throw new Error("invalid geometry", { cause: error });
      }
      eq(g.schemaVersion, 1);
      eq(g.pages.length, 2);
      for (const [i, p] of g.pages.entries()) {
        if (Number(process.env.LUA_EXPECTED_ABI || 4) === 5) {
          eq(p.page, i + 1);
          eq(p.status, "available");
          eq(p.reason, "none");
          eq(p.writerToPdfRaw.length, 6);
          eq(p.writerPageBoxBp.length, 4);
        } else
          assert.deepEqual(p, {
            page: i + 1,
            status: "unsupported",
            reason: "writer-origin-unavailable",
            writerToPdfRaw: null,
            writerPageBoxBp: null,
          });
      }
      checks += g.pages.length;
      for (const [length, copy, max] of [
        [raw.length, "oc_synctex_copy", 1048575],
        [geometry.length, "oc_synctex_geometry_copy", 131072],
      ]) {
        const p = m._malloc(length);
        m.HEAPU8.fill(0x5a, p, p + length);
        for (const [t, c] of [
          [tag, length - 1],
          [tag + 1, length],
          [tag, max + 1],
        ]) {
          eq(call(copy, ["number", "number", "number"], [t, p, c]), 0);
          eq(
            m.HEAPU8.subarray(p, p + length).every((b) => b === 0x5a),
            true,
            "complete-or-none",
          );
        }
        m._free(p);
      }
      fs.writeFileSync(path.join(output, mode + ".synctex"), raw);
      fs.writeFileSync(path.join(output, mode + ".geometry.json"), geometry);
      if (mode === "post-config") eq(config(1, 1048575), 0);
      else if (mode === "post-invalid-root")
        eq(
          call(
            "oc_luatex_configure",
            ["string", "string"],
            ["bad.tex", "again"],
          ),
          -2,
        );
      else if (mode === "post-budget") {
        assert.throws(() =>
          m.FS.writeFile("/work/output/overrun.aux", new Uint8Array(8388609)),
        );
        ++checks;
      } else
        eq(
          call("oc_luatex_compile_latex"),
          -2,
          "rejected repeat revokes all previous bytes",
        );
      eq(maplen(tag), 0);
      eq(geolen(tag), 0);
      eq(call("oc_synctex_status"), 2);
    }
  }
  const receipt = {
    schemaVersion: 1,
    mode,
    status: "pass",
    nativeExit: status,
    checks,
    skips: 0,
    identities,
    files,
    budget,
    outputs: {
      pdf: { length: pdf.length, sha256: sha(pdf) },
      map: { length: raw.length, sha256: sha(raw) },
      geometry: { length: geometry.length, sha256: sha(geometry) },
    },
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
