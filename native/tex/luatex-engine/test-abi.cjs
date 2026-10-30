"use strict";
const fs = require("node:fs");
const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const candidate = process.env.CANDIDATE || "/control/candidate";
const create = require(candidate + "/luatex.js");
const wasmBytes = fs.readFileSync(candidate + "/luatex.wasm");
const wasm = new WebAssembly.Module(wasmBytes);
const sha = (bytes) => crypto.createHash("sha256").update(bytes).digest("hex");
let checks = 0;
async function fresh() {
  return create({
    noInitialRun: true,
    print() {},
    printErr() {},
    instantiateWasm(imports, done) {
      const i = new WebAssembly.Instance(wasm, imports);
      done(i);
      return i.exports;
    },
  });
}
const call = (m, n, t, a) => m.ccall("oc_luatex_" + n, "number", t, a);
(async () => {
  for (const name of [
    "",
    "../bad.tex",
    "/host.tex",
    "a/../bad.tex",
    "a//b.tex",
    "a\\b.tex",
    'a"b.tex',
    "a|b.tex",
    "x\n.tex",
    ".",
    "a/".repeat(125),
    ...Array.from("{}%#^~$&'!*?[]", (c) => "a" + c + ".tex"),
  ]) {
    const m = await fresh();
    assert.equal(
      call(m, "configure", ["string", "string"], [name, "revision"]),
      -1,
      name,
    );
    ++checks;
  }
  for (const bytes of [
    [0x80],
    [0xc0, 0xaf],
    [0xc1, 0xbf],
    [0xe0, 0x80, 0xaf],
    [0xed, 0xa0, 0x80],
    [0xf0, 0x80, 0x80, 0xaf],
    [0xf4, 0x90, 0x80, 0x80],
    [0xf5, 0x80, 0x80, 0x80],
    [0xe2, 0x82],
  ]) {
    const invalid = await fresh();
    const p = invalid._malloc(bytes.length + 1);
    invalid.HEAPU8.set([...bytes, 0], p);
    assert.equal(
      call(invalid, "configure", ["number", "string"], [p, "revision"]),
      -1,
    );
    ++checks;
    invalid._free(p);
  }
  const declared = await fresh();
  assert.equal(call(declared, "abi", [], []), 3);
  ++checks;
  assert.match(
    declared.ccall("oc_luatex_version", "string", [], []),
    /browser ABI 3/,
  );
  ++checks;
  assert.equal(
    call(
      declared,
      "configure",
      ["string", "string"],
      ["розділ/теза 😀.tex", "revision"],
    ),
    0,
  );
  ++checks;
  for (const name of ["result-blx.bib", "result.bcf", "result.run.xml"]) {
    assert.equal(
      call(
        declared,
        "add_file",
        ["number", "string", "number", "number"],
        [2, name, 0, 0],
      ),
      -1,
    );
    ++checks;
    assert.equal(call(declared, "declare_output", ["string"], [name]), 0);
    ++checks;
    const bytes = Buffer.from("Ukrainian Україна 😀\u0000" + name);
    const p = declared._malloc(bytes.length);
    declared.HEAPU8.set(bytes, p);
    assert.equal(
      call(
        declared,
        "add_file",
        ["number", "string", "number", "number"],
        [2, name, p, bytes.length],
      ),
      0,
    );
    ++checks;
    declared._free(p);
    assert.deepEqual(
      Buffer.from(declared.FS.readFile("/work/output/" + name)),
      bytes,
    );
    ++checks;
    assert.equal(call(declared, "declare_output", ["string"], [name]), 0);
    ++checks;
  }
  for (let i = 0; i < 115; ++i) {
    assert.equal(
      call(
        declared,
        "declare_output",
        ["string"],
        ["chapters/part" + i + ".aux"],
      ),
      0,
    );
    ++checks;
  }
  assert.equal(
    call(declared, "declare_output", ["string"], ["one-too-many.aux"]),
    -4,
  );
  ++checks;
  assert.equal(
    call(declared, "declare_output", ["string"], ["arbitrary.pdf"]),
    -1,
  );
  ++checks;
  assert.equal(
    call(
      declared,
      "add_file",
      ["number", "string", "number", "number"],
      [2, "undeclared.aux", 0, 0],
    ),
    -1,
  );
  ++checks;
  assert.equal(
    call(
      declared,
      "add_file",
      ["number", "string", "number", "number"],
      [2, "chapters/part0.aux", 0, 0],
    ),
    0,
  );
  ++checks;
  assert.ok(declared.FS.analyzePath("/work/output/chapters").exists);
  ++checks;
  const invalidNames = [
    "arbitrary.bib",
    "result.bib",
    "other-blx.bib",
    "other.bcf",
    "other.run.xml",
    "result.xml",
    "result.run.XML",
    "RESULT.bcf",
    "nested/result-blx.bib",
    "nested/result.bcf",
    "nested/result.run.xml",
    "../result.bcf",
    "a/../result.run.xml",
    "a//result-blx.bib",
    "/result.bcf",
    "a\\result.bcf",
    "__oop_runtime__/part.aux",
    "__OOP_RUNTIME__/part.aux",
  ];
  for (const name of invalidNames) {
    const n = await fresh();
    assert.equal(
      call(n, "configure", ["string", "string"], ["main.tex", "revision"]),
      0,
    );
    assert.equal(call(n, "declare_output", ["string"], [name]), -1, name);
    ++checks;
    assert.equal(
      call(
        n,
        "add_file",
        ["number", "string", "number", "number"],
        [2, name, 0, 0],
      ),
      name.includes("..") ||
        name.startsWith("/") ||
        name.includes("\\") ||
        name.includes("//") ||
        name.includes("__")
        ? -4
        : -1,
      name,
    );
    ++checks;
  }
  for (const name of [
    "result-blx.bib",
    "result.bcf",
    "result.run.xml",
    "parts/chapter.aux",
  ]) {
    for (const projectFirst of [true, false]) {
      const n = await fresh();
      assert.equal(
        call(n, "configure", ["string", "string"], ["main.tex", "revision"]),
        0,
      );
      const bytes = Buffer.from("captured project stays unchanged");
      const p = n._malloc(bytes.length);
      n.HEAPU8.set(bytes, p);
      if (projectFirst) {
        assert.equal(
          call(
            n,
            "add_file",
            ["number", "string", "number", "number"],
            [1, name, p, bytes.length],
          ),
          0,
        );
        ++checks;
        assert.equal(call(n, "declare_output", ["string"], [name]), -1);
        ++checks;
        assert.deepEqual(
          Buffer.from(n.FS.readFile("/work/project/" + name)),
          bytes,
        );
        ++checks;
        assert.equal(n.FS.analyzePath("/work/output/" + name).exists, false);
        ++checks;
      } else {
        assert.equal(call(n, "declare_output", ["string"], [name]), 0);
        ++checks;
        assert.equal(
          call(
            n,
            "add_file",
            ["number", "string", "number", "number"],
            [1, name, p, bytes.length],
          ),
          -1,
        );
        ++checks;
        assert.equal(n.FS.analyzePath("/work/project/" + name).exists, false);
        ++checks;
      }
      n._free(p);
    }
  }
  const aliases = await fresh();
  assert.equal(
    call(aliases, "configure", ["string", "string"], ["main.tex", "revision"]),
    0,
  );
  for (const name of [
    "RESULT.BCF",
    "RESULT-BLX.BIB",
    "Result.Run.Xml",
    "result.aux",
    "result.bcf/child.tex",
  ]) {
    if (name === "result.bcf/child.tex") continue; // Below, with BCF explicitly declared.
    if (name !== "result.aux")
      assert.equal(
        call(
          aliases,
          "add_file",
          ["number", "string", "number", "number"],
          [1, name, 0, 0],
        ),
        0,
      );
    const exact =
      name === "RESULT.BCF"
        ? "result.bcf"
        : name === "RESULT-BLX.BIB"
          ? "result-blx.bib"
          : name === "Result.Run.Xml"
            ? "result.run.xml"
            : name;
    assert.equal(
      call(aliases, "declare_output", ["string"], [exact]),
      name === "result.aux" ? 0 : -1,
    );
    ++checks;
    assert.equal(
      call(
        aliases,
        "add_file",
        ["number", "string", "number", "number"],
        [1, "result.aux", 0, 0],
      ),
      -1,
    );
    ++checks;
  }
  const tree = await fresh();
  assert.equal(
    call(tree, "configure", ["string", "string"], ["main.tex", "revision"]),
    0,
  );
  assert.equal(call(tree, "declare_output", ["string"], ["result.bcf"]), 0);
  for (const name of [
    "result.bcf/child.tex",
    "RESULT.BCF",
    "__oop_runtime__/secret.tex",
  ]) {
    assert.ok(
      call(
        tree,
        "add_file",
        ["number", "string", "number", "number"],
        [1, name, 0, 0],
      ) < 0,
      name,
    );
    ++checks;
  }
  for (const bytes of [
    [0x80],
    [0xed, 0xa0, 0x80],
    [0xf4, 0x90, 0x80, 0x80],
    [0xe2, 0x82],
  ]) {
    const p = tree._malloc(bytes.length + 1);
    tree.HEAPU8.set([...bytes, 0], p);
    assert.equal(call(tree, "declare_output", ["number"], [p]), -1);
    ++checks;
    assert.equal(
      call(
        tree,
        "add_file",
        ["number", "number", "number", "number"],
        [1, p, 0, 0],
      ),
      -4,
    );
    ++checks;
    tree._free(p);
  }
  const unicode = await fresh();
  assert.equal(
    call(
      unicode,
      "configure",
      ["string", "string"],
      ["тези/праця 😀.tex", "revision"],
    ),
    0,
  );
  ++checks;
  assert.equal(
    call(
      unicode,
      "add_file",
      ["number", "string", "number", "number"],
      [1, "Розділи/Праця.aux", 0, 0],
    ),
    0,
  );
  ++checks;
  assert.equal(
    call(unicode, "declare_output", ["string"], ["розділи/праця.aux"]),
    -1,
  );
  ++checks;
  assert.equal(
    call(unicode, "declare_output", ["string"], ["додаток/Київ.aux"]),
    0,
  );
  ++checks;
  assert.equal(
    call(
      unicode,
      "add_file",
      ["number", "string", "number", "number"],
      [1, "ДОДАТОК/київ.aux", 0, 0],
    ),
    -1,
  );
  ++checks;
  assert.equal(
    call(unicode, "declare_output", ["string"], ["додаток/Київ.aux/child.aux"]),
    -1,
  );
  ++checks;
  // NFC is a comparison key only. Native storage retains a unique spelling,
  // including decomposed Ukrainian breve and Latin acute sequences.
  for (const decomposed of ["e\u0301", "и\u0306", "розділи/и\u0306-e\u0301"]) {
    const composed = decomposed.normalize("NFC");
    assert.notEqual(decomposed, composed);
    ++checks;
    for (const [first, alias] of [
      [decomposed, composed],
      [composed, decomposed],
    ]) {
      for (const kind of [0, 1]) {
        const n = await fresh();
        assert.equal(
          call(
            n,
            "configure",
            ["string", "string"],
            [first + ".tex", "revision"],
          ),
          0,
        );
        ++checks;
        const bytes = Buffer.from("Exact captured bytes Україна 😀");
        const p = n._malloc(bytes.length);
        n.HEAPU8.set(bytes, p);
        assert.equal(
          call(
            n,
            "add_file",
            ["number", "string", "number", "number"],
            [kind, first + ".tex", p, bytes.length],
          ),
          0,
        );
        ++checks;
        assert.equal(
          call(
            n,
            "add_file",
            ["number", "string", "number", "number"],
            [kind, alias + ".tex", p, bytes.length],
          ),
          -1,
        );
        ++checks;
        const base = kind === 0 ? "/work/runtime/" : "/work/project/";
        assert.deepEqual(
          Buffer.from(n.FS.readFile(base + first + ".tex")),
          bytes,
        );
        ++checks;
        assert.equal(n.FS.analyzePath(base + alias + ".tex").exists, false);
        ++checks;
        n._free(p);
      }
      for (const projectFirst of [true, false]) {
        const n = await fresh();
        assert.equal(
          call(n, "configure", ["string", "string"], ["main.tex", "revision"]),
          0,
        );
        ++checks;
        if (projectFirst) {
          assert.equal(
            call(
              n,
              "add_file",
              ["number", "string", "number", "number"],
              [1, first + ".aux", 0, 0],
            ),
            0,
          );
          ++checks;
          assert.equal(
            call(n, "declare_output", ["string"], [alias + ".aux"]),
            -1,
          );
          ++checks;
          assert.equal(
            n.FS.analyzePath("/work/output/" + alias + ".aux").exists,
            false,
          );
          ++checks;
        } else {
          assert.equal(
            call(n, "declare_output", ["string"], [first + ".aux"]),
            0,
          );
          ++checks;
          assert.equal(
            call(
              n,
              "add_file",
              ["number", "string", "number", "number"],
              [1, alias + ".aux", 0, 0],
            ),
            -1,
          );
          ++checks;
          assert.equal(
            call(n, "declare_output", ["string"], [alias + ".aux"]),
            -1,
          );
          ++checks;
          assert.equal(
            call(
              n,
              "add_file",
              ["number", "string", "number", "number"],
              [2, first + ".aux", 0, 0],
            ),
            0,
          );
          ++checks;
          assert.equal(
            n.FS.analyzePath("/work/output/" + first + ".aux").exists,
            true,
          );
          ++checks;
        }
      }
    }
  }
  // Document the native full-uppercase guard conservatively, not .NET parity.
  const fullCase = await fresh();
  assert.equal(
    call(fullCase, "configure", ["string", "string"], ["main.tex", "revision"]),
    0,
  );
  ++checks;
  assert.equal(
    call(
      fullCase,
      "add_file",
      ["number", "string", "number", "number"],
      [1, "straße.tex", 0, 0],
    ),
    0,
  );
  ++checks;
  assert.equal(
    call(
      fullCase,
      "add_file",
      ["number", "string", "number", "number"],
      [1, "STRASSE.tex", 0, 0],
    ),
    -1,
  );
  ++checks;
  const boundary = await fresh();
  assert.equal(
    call(boundary, "configure", ["string", "string"], ["main.tex", "revision"]),
    0,
  );
  const carryBuffer = boundary._malloc(8388609);
  for (const name of ["result-blx.bib", "result.bcf", "result.run.xml"]) {
    assert.equal(call(boundary, "declare_output", ["string"], [name]), 0);
    ++checks;
    assert.equal(
      call(
        boundary,
        "add_file",
        ["number", "string", "number", "number"],
        [2, name, carryBuffer, 8388609],
      ),
      -4,
    );
    ++checks;
    assert.equal(boundary.FS.analyzePath("/work/output/" + name).exists, false);
    ++checks;
    assert.equal(
      call(
        boundary,
        "add_file",
        ["number", "string", "number", "number"],
        [2, name, carryBuffer, 8388608],
      ),
      0,
    );
    ++checks;
    assert.equal(boundary.FS.stat("/work/output/" + name).size, 8388608);
    ++checks;
  }
  boundary._free(carryBuffer);
  const fileCount = await fresh();
  assert.equal(
    call(
      fileCount,
      "configure",
      ["string", "string"],
      ["main.tex", "revision"],
    ),
    0,
  );
  for (let i = 0; i < 4608; ++i)
    assert.equal(
      call(
        fileCount,
        "add_file",
        ["number", "string", "number", "number"],
        [0, "empty" + i, 0, 0],
      ),
      0,
    );
  checks += 4608;
  assert.equal(
    call(
      fileCount,
      "add_file",
      ["number", "string", "number", "number"],
      [0, "too-many", 0, 0],
    ),
    -4,
  );
  ++checks;
  assert.equal(
    fileCount.FS.analyzePath("/work/runtime/too-many").exists,
    false,
  );
  ++checks;
  const quota = await fresh();
  assert.equal(
    call(quota, "configure", ["string", "string"], ["main.tex", "revision"]),
    0,
  );
  const allocation = quota._malloc(32 * 1024 * 1024);
  for (let i = 0; i < 3; ++i) {
    assert.equal(
      call(
        quota,
        "add_file",
        ["number", "string", "number", "number"],
        [0, "data" + i, allocation, 32 * 1024 * 1024],
      ),
      0,
    );
    ++checks;
  }
  assert.equal(
    call(
      quota,
      "add_file",
      ["number", "string", "number", "number"],
      [0, "overflow", allocation, 1],
    ),
    -4,
  );
  ++checks;
  assert.equal(quota.FS.analyzePath("/work/runtime/overflow").exists, false);
  ++checks;
  quota._free(allocation);
  const m = await fresh();
  assert.equal(
    call(
      m,
      "configure",
      ["string", "string"],
      ["розділ/my thesis.tex", "revision"],
    ),
    0,
  );
  ++checks;
  assert.equal(call(m, "declare_output", ["string"], ["file.aux"]), 0);
  ++checks;
  assert.equal(call(m, "declare_output", ["string"], ["file.aux/sub.aux"]), -1);
  ++checks;
  assert.equal(
    call(m, "configure", ["string", "string"], ["other.tex", "new"]),
    -2,
  );
  ++checks;
  assert.equal(call(m, "output_size", ["number"], [0]), -2);
  ++checks;
  const seed = (kind, name, length = 0) =>
    call(
      m,
      "add_file",
      ["number", "string", "number", "number"],
      [kind, name, 0, length],
    );
  assert.equal(seed(1, "valid.tex"), 0);
  ++checks;
  for (const name of [
    "result.aux",
    "result.toc",
    "result.out",
    "result.bbl",
    "result.blg",
    "result.lof",
    "result.lot",
  ]) {
    assert.equal(seed(2, name), 0, name);
    ++checks;
  }
  assert.equal(seed(1, "valid.tex"), -1);
  ++checks;
  assert.equal(seed(1, "../host"), -4);
  ++checks;
  assert.equal(seed(2, "result.pdf"), -1);
  ++checks;
  assert.equal(seed(9, "other"), -1);
  ++checks;
  assert.equal(seed(1, "too-big", 32 * 1024 * 1024 + 1), -4);
  ++checks;
  const big = m._malloc(8388609);
  assert.ok(big);
  ++checks;
  assert.equal(
    call(
      m,
      "add_file",
      ["number", "string", "number", "number"],
      [1, "too-large-project.tex", big, 8388609],
    ),
    -4,
  );
  ++checks;
  assert.equal(
    call(
      m,
      "add_file",
      ["number", "string", "number", "number"],
      [2, "result.aux", big, 8388609],
    ),
    -4,
  );
  ++checks;
  assert.equal(
    call(
      m,
      "add_file",
      ["number", "string", "number", "number"],
      [0, "large-runtime.bin", big, 8388609],
    ),
    0,
  );
  ++checks;
  m._free(big);
  m.FS.writeFile(
    "/work/output/result.pdf",
    Buffer.from("%PDF-1.7\nSTALE\n%%EOF"),
  );
  assert.notEqual(call(m, "compile_latex", [], []), 0);
  ++checks;
  assert.equal(call(m, "output_size", ["number"], [0]), 0);
  ++checks;
  assert.equal(call(m, "compile_format", [], []), -2);
  ++checks;
  assert.equal(call(m, "declare_output", ["string"], ["late.aux"]), -2);
  ++checks;
  assert.equal(seed(1, "late.tex"), -2);
  ++checks;
  assert.equal(m.FS.analyzePath("/etc/passwd").exists, false);
  ++checks;
  console.log(
    JSON.stringify({
      passed: checks,
      suite: "actual WASM ABI",
      wasmSha256: sha(wasmBytes),
      loaderSha256: sha(fs.readFileSync(candidate + "/luatex.js")),
      memoryBytes: m.HEAPU8.byteLength,
    }),
  );
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
