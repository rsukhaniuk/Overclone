// Test tooling only. Product orchestration remains C#; a parent worker must
// instantiate one module per attempt, deny fetch/host mounts and terminate it.
"use strict";
const fs = require("node:fs");
const path = require("node:path");
const crypto = require("node:crypto");
const assert = require("node:assert/strict");
const candidate = process.env.CANDIDATE || "/control/candidate";
const supplied = process.env.TEXMF_INPUT || "/control/provided-texmf";
const suiteInput = process.env.SUITE_INPUT;
const manifestPath = path.join(
  suiteInput || supplied,
  suiteInput ? "pins.json" : "manifest.json",
);
const results = process.env.RESULTS || "/control/results";
const mode = process.argv[2] || "format";
const label = process.argv[3] || mode;
const sha = (bytes) => crypto.createHash("sha256").update(bytes).digest("hex");
const manifestBytes = fs.readFileSync(manifestPath);
assert.equal(
  sha(manifestBytes),
  suiteInput
    ? process.env.SUITE_PINS_SHA256
    : "959cf3e4615b85fa8988a178b8e90f7ea20c92bcadbae411f3f4b346594532b1",
);
const lines = [];
const { parentPort } = require("node:worker_threads");
const loaderSha256 = sha(fs.readFileSync(path.join(candidate, "luatex.js")));
const create = require(path.join(candidate, "luatex.js"));
(async () => {
  let manifest;
  try {
    manifest = JSON.parse(manifestBytes);
  } catch (error) {
    throw new Error("Invalid supplied manifest JSON", { cause: error });
  }
  const started = Date.now();
  const wasm = fs.readFileSync(path.join(candidate, "luatex.wasm"));
  const m = await create({
    noInitialRun: true,
    instantiateWasm(imports, success) {
      const instance = new WebAssembly.Instance(
        new WebAssembly.Module(wasm),
        imports,
      );
      success(instance);
      return instance.exports;
    },
    print: (line) => {
      lines.push(line);
      if (parentPort && line.includes("RUNAWAY-BEGIN"))
        parentPort.postMessage({ runaway: true });
    },
    printErr: (line) => {
      lines.push(line);
    },
  });
  const call = (name, types, args, result = "number") =>
    m.ccall("oc_luatex_" + name, result, types, args);
  const error = () => call("error", [], [], "string");
  const expectedAbi = Number(process.env.LUA_ABI || 3);
  assert.ok(expectedAbi === 3 || expectedAbi === 4 || expectedAbi === 5);
  assert.equal(call("abi", [], []), expectedAbi);
  assert.match(call("version", [], [], "string"), /LuaTeX 1\.13\.0/);
  const root = process.env.ROOT_NAME || "розділ/my thesis.tex";
  assert.equal(
    call("configure", ["string", "string"], [root, "synthetic-revision-1"]),
    0,
    error(),
  );
  assert.equal(call("context", [], [], "string"), "synthetic-revision-1");
  const seed = (kind, name, bytes) => {
    const p = m._malloc(bytes.length || 1);
    m.HEAPU8.set(bytes, p);
    const code = call(
      "add_file",
      ["number", "string", "number", "number"],
      [kind, name, p, bytes.length],
    );
    m._free(p);
    assert.equal(code, 0, `${name}: ${error()}`);
  };
  if (suiteInput) {
    for (const entry of manifest) {
      // Data only; engine/converter/query/renderer bytes and other formats never
      // become Lua runtime inputs. The exact supplied format is seeded below.
      if (
        /\.(js|wasm|fmt)$/.test(entry.name) ||
        entry.name.startsWith("notices/")
      )
        continue;
      const bytes = fs.readFileSync(path.join(suiteInput, "files", entry.name));
      assert.equal(bytes.length, entry.length, entry.name);
      assert.equal(sha(bytes), entry.sha256, entry.name);
      seed(0, entry.name, bytes);
    }
  } else {
    for (const entry of manifest.files) {
      if (entry.name.startsWith("notices/")) continue;
      const bytes = fs.readFileSync(path.join(supplied, "files", entry.name));
      assert.equal(bytes.length, entry.length, entry.name);
      assert.equal(sha(bytes), entry.sha256, entry.name);
      seed(0, entry.name, bytes);
    }
    let oldPins;
    try {
      oldPins = JSON.parse(fs.readFileSync("/inputs/resources/pins.json"));
    } catch (error) {
      throw new Error("Invalid approved resource pins", { cause: error });
    }
    for (const entry of oldPins) {
      if (!/\.(tfm|pfb|map|enc)$/.test(entry.name)) continue;
      const bytes = fs.readFileSync("/inputs/resources/files/" + entry.name);
      assert.equal(bytes.length, entry.length);
      assert.equal(sha(bytes), entry.sha256);
      seed(0, "extra/" + entry.name, bytes);
    }
    for (const [directory, prefix] of [
      ["/control/provided-lua-unicode", "texlive/tex/luatex/lua-uni-algos/"],
      ["/control/provided-font-extra", "extra/"],
    ]) {
      let supplement;
      try {
        supplement = JSON.parse(fs.readFileSync(directory + "/manifest.json"));
      } catch (error) {
        throw new Error("Invalid supplement manifest", { cause: error });
      }
      for (const entry of supplement.files) {
        const bytes = fs.readFileSync(directory + "/files/" + entry.name);
        assert.equal(bytes.length, entry.length);
        assert.equal(sha(bytes), entry.sha256);
        seed(0, prefix + entry.name, bytes);
      }
    }
  }
  const declaredNames = (process.env.OUTPUT_NAMES || "")
    .split(",")
    .filter(Boolean);
  for (const name of declaredNames)
    assert.equal(call("declare_output", ["string"], [name]), 0, error());
  const carryInputs = {};
  for (const [variable, kind] of [
    ["RUNTIME_MANIFEST", 0],
    ["PROJECT_MANIFEST", 1],
    ["CARRY_MANIFEST", 2],
  ]) {
    if (!process.env[variable]) continue;
    let input;
    try {
      input = JSON.parse(fs.readFileSync(process.env[variable]));
    } catch (error) {
      throw new Error("Invalid explicit input manifest: " + variable, {
        cause: error,
      });
    }
    for (const entry of input.files) {
      const bytes = fs.readFileSync(
        path.resolve(path.dirname(process.env[variable]), entry.file),
      );
      assert.equal(bytes.length, entry.length);
      assert.equal(sha(bytes), entry.sha256);
      seed(kind, entry.name, bytes);
      if (kind === 2) {
        assert.deepEqual(
          Buffer.from(m.FS.readFile("/work/output/" + entry.name)),
          bytes,
        );
        carryInputs[entry.name] = { length: bytes.length, sha256: sha(bytes) };
      }
    }
  }
  if (mode !== "format") {
    seed(
      0,
      "lualatex.fmt",
      fs.readFileSync(
        process.env.FORMAT_FILE || path.join(candidate, "lualatex.fmt"),
      ),
    );
    const source = process.env.SOURCE_FILE
      ? fs.readFileSync(process.env.SOURCE_FILE)
      : Buffer.from(String.raw`\documentclass{article}
\usepackage{fontspec}
\setmainfont{DejaVuSerif.ttf}[Path=/work/runtime/extra/,BoldFont=DejaVuSerif-Bold.ttf,ItalicFont=DejaVuSerif-Italic.ttf,BoldItalicFont=DejaVuSerif-BoldItalic.ttf]
\begin{document}
Український текст. \textbf{Дипломна робота.} \textit{Київ.}
\input{chapter}
Reference: \ref{sec:test}.
\end{document}
`);
    seed(1, root, source);
    if (process.env.LUA_PAYLOAD) {
      const payload = fs.readFileSync(process.env.LUA_PAYLOAD);
      seed(1, "attack.lua", payload);
      fs.writeFileSync(path.join(results, label + ".attack.lua"), payload);
    }
    fs.writeFileSync(path.join(results, label + ".source.tex"), source);
    seed(
      1,
      process.env.INCLUDE === "1" ? "chapters/chapter.tex" : "chapter.tex",
      Buffer.from("\\section{Перевірка}\\label{sec:test}\nMulti-file ALPHA.\n"),
    );
    if (process.env.INCLUDE === "1")
      assert.equal(
        call("declare_output", ["string"], ["chapters/chapter.aux"]),
        0,
        error(),
      );
    if (process.env.AUX_FILE)
      seed(2, "result.aux", fs.readFileSync(process.env.AUX_FILE));
    if (process.env.CHAPTER_AUX_FILE)
      seed(
        2,
        "chapters/chapter.aux",
        fs.readFileSync(process.env.CHAPTER_AUX_FILE),
      );
    // An unrelated preexisting PDF must never authorize failure or missing output.
    m.FS.writeFile(
      "/work/output/result.pdf",
      Buffer.from("%PDF-1.7\nSTALE\n%%EOF"),
    );
  }
  const pathAliases = [];
  if (process.env.PATH_ALIAS_MANIFEST) {
    let aliases;
    try {
      aliases = JSON.parse(fs.readFileSync(process.env.PATH_ALIAS_MANIFEST));
    } catch (error) {
      throw new Error("Invalid path alias manifest", { cause: error });
    }
    for (const { kind, original, alias } of aliases) {
      assert.ok(kind === 0 || kind === 1);
      assert.notEqual(original, alias);
      assert.equal(original.normalize("NFC"), alias.normalize("NFC"));
      const base = kind === 0 ? "/work/runtime/" : "/work/project/";
      const bytes = Buffer.from(m.FS.readFile(base + original));
      const p = m._malloc(bytes.length || 1);
      m.HEAPU8.set(bytes, p);
      assert.equal(
        call(
          "add_file",
          ["number", "string", "number", "number"],
          [kind, alias, p, bytes.length],
        ),
        -1,
      );
      m._free(p);
      assert.deepEqual(Buffer.from(m.FS.readFile(base + original)), bytes);
      assert.equal(m.FS.analyzePath(base + alias).exists, false);
      pathAliases.push({ kind, original, alias, sha256: sha(bytes) });
    }
  }
  if (process.env.TAMPER_VFS_SHAPE === "1") {
    const originalWrite = m.FS.write;
    m.FS.write = (...args) => originalWrite(...args); // Semantic equivalent still must fail exact-shape/ref validation.
  }
  const aliasStream =
    process.env.POST_CASE === "alias"
      ? m.FS.open("/work/output/alias.aux", "w")
      : null;
  if (parentPort) parentPort.postMessage({ compiling: true });
  const status = call(
    mode === "format" ? "compile_format" : "compile_latex",
    [],
    [],
  );
  if (expectedAbi === 4 || expectedAbi === 5) {
    // Existing ABI3 regression flows never opt into maps, including AUX/BCF carry.
    assert.equal(m._oc_synctex_length(401), 0);
    assert.equal(m._oc_synctex_geometry_length(401), 0);
  }
  let postCase = null;
  if (process.env.POST_CASE) {
    assert.equal(
      status,
      0,
      "lower-hook controls start with an actual successful native PDF",
    );
    const name = process.env.POST_CASE;
    const before = m.FS.stat("/work/project/chapter.tex").size;
    let refused = false;
    try {
      if (name === "mmap") {
        const stream = m.FS.open("/work/project/chapter.tex", "r");
        try {
          m.FS.mmap(stream, 65536, 0, 1, 2);
        } finally {
          m.FS.close(stream);
        }
      } else if (name === "msync") {
        const stream = m.FS.open("/work/project/chapter.tex", "r+");
        try {
          m.FS.msync(stream, new Uint8Array(8388609), 0, 8388609, 0);
        } finally {
          m.FS.close(stream);
        }
      } else if (name === "truncate")
        m.FS.truncate("/work/project/chapter.tex", 8388609);
      else if (name === "writefile")
        m.FS.writeFile("/work/project/chapter.tex", new Uint8Array(8388609));
      else if (name === "copy") {
        m.FS.readFile("/work/runtime/lualatex.fmt");
        m.FS.readFile("/work/runtime/lualatex.fmt");
      } else if (name === "ownership")
        m.FS.writeFile(
          "/work/output/owned.aux",
          new Uint8Array(20 * 1024 * 1024).subarray(0, 1),
          { canOwn: true },
        );
      else if (name === "alias")
        aliasStream.stream_ops.write(
          aliasStream,
          new Uint8Array(1),
          0,
          1,
          8388608,
          false,
        );
      else if (name === "mount") m.FS.mount({}, {}, "/work/mount");
      else if (name === "readonly")
        m.FS.open("/work/runtime/lualatex.fmt", "w");
      else if (name === "unlink") m.FS.unlink("/work/runtime/lualatex.fmt");
      else throw new Error("unknown post case");
    } catch (error) {
      assert.equal(error.errno, 51, "actual pinned SDK ENOSPC failure");
      refused = true;
    }
    assert.ok(refused, name + " must be refused");
    assert.equal(
      m.FS.stat("/work/project/chapter.tex").size,
      before,
      "preflight preserves existing bytes",
    );
    if (name === "ownership")
      assert.equal(m.FS.analyzePath("/work/output/owned.aux").exists, false);
    assert.equal(
      call("output_size", ["number"], [0]),
      0,
      "quota latch revokes already-completed PDF before reuse",
    );
    assert.equal(
      call("copy_output", ["number", "number", "number"], [0, 0, 0]),
      0,
    );
    assert.equal(call("named_output_size", ["string"], ["result.pdf"]), 0);
    assert.equal(
      call(
        "copy_named_output",
        ["string", "number", "number"],
        ["result.pdf", 0, 0],
      ),
      0,
    );
    postCase = {
      name,
      refused: true,
      revokedBeforeReuse: true,
      scope: "actual SDK lower hook; no Lua mmap API is registered",
    };
  }
  const recorderName = mode === "format" ? "lualatex.fls" : "result.fls";
  let recorderError = null;
  try {
    if (m.FS.analyzePath("/work/output/" + recorderName).exists)
      fs.writeFileSync(
        path.join(results, label + ".fls"),
        m.FS.readFile("/work/output/" + recorderName),
      );
  } catch (error) {
    recorderError = String(error);
  } // No unbounded FS dump after a quota failure.
  const artifacts = {};
  for (const [id, extension] of [
    "pdf",
    "log",
    "fmt",
    "aux",
    "toc",
    "out",
    "bbl",
    "blg",
    "lof",
    "lot",
  ].entries()) {
    const size = call("output_size", ["number"], [id]);
    if (size > 0) {
      const p = m._malloc(size);
      assert.equal(
        call("copy_output", ["number", "number", "number"], [id, p, size]),
        size,
      );
      const bytes = Buffer.from(m.HEAPU8.slice(p, p + size));
      m._free(p);
      fs.writeFileSync(path.join(results, label + "." + extension), bytes);
      artifacts[extension] = { length: size, sha256: sha(bytes) };
      if (mode === "format" && extension === "fmt")
        fs.writeFileSync(path.join(candidate, "lualatex.fmt"), bytes);
      if (extension === "pdf") {
        assert.equal(bytes.subarray(0, 5).toString(), "%PDF-");
        assert.ok(bytes.subarray(-1024).includes(Buffer.from("%%EOF")));
        assert.ok(!bytes.includes(Buffer.from("STALE")));
      }
    }
  }
  if (process.env.INCLUDE === "1" && status === 0) {
    const size = call(
      "named_output_size",
      ["string"],
      ["chapters/chapter.aux"],
    );
    assert.ok(size > 0);
    const p = m._malloc(size);
    assert.equal(
      call(
        "copy_named_output",
        ["string", "number", "number"],
        ["chapters/chapter.aux", p, size],
      ),
      size,
    );
    const bytes = Buffer.from(m.HEAPU8.slice(p, p + size));
    m._free(p);
    fs.writeFileSync(path.join(results, label + ".chapter.aux"), bytes);
    artifacts.chapterAux = { length: size, sha256: sha(bytes) };
  }
  for (const name of declaredNames) {
    const size = call("named_output_size", ["string"], [name]);
    if (status !== 0 || postCase)
      assert.equal(
        size,
        0,
        "failure revokes named state before reuse: " + name,
      );
    if (size <= 0) continue;
    const p = m._malloc(size);
    assert.equal(
      call(
        "copy_named_output",
        ["string", "number", "number"],
        [name, p, size],
      ),
      size,
    );
    const bytes = Buffer.from(m.HEAPU8.slice(p, p + size));
    m._free(p);
    const file = label + ".named-" + name.replaceAll("/", "_");
    fs.writeFileSync(path.join(results, file), bytes);
    artifacts[name] = { length: size, sha256: sha(bytes), file };
  }
  const nativeError = error();
  if (process.env.API_ERROR_AFTER_SUCCESS === "1") {
    assert.equal(status, 0);
    assert.equal(call("declare_output", ["string"], ["late.aux"]), -2);
    assert.equal(
      call("output_size", ["number"], [0]),
      0,
      "late declaration error revokes PDF",
    );
    for (const name of declaredNames) {
      assert.equal(
        call("named_output_size", ["string"], [name]),
        0,
        "late error revokes carried state",
      );
      assert.equal(
        call("copy_named_output", ["string", "number", "number"], [name, 0, 0]),
        0,
      );
    }
  }
  assert.equal(call("compile_latex", [], []), -2, "reused module rejected");
  assert.equal(
    call("output_size", ["number"], [0]),
    0,
    "reuse revokes prior successful PDF",
  );
  assert.equal(
    call("copy_output", ["number", "number", "number"], [0, 0, 0]),
    0,
  );
  assert.equal(
    call("output_size", ["number"], [2]),
    0,
    "reuse revokes prior successful format",
  );
  assert.equal(
    call("copy_output", ["number", "number", "number"], [2, 0, 0]),
    0,
  );
  assert.equal(call("named_output_size", ["string"], ["result.pdf"]), 0);
  assert.equal(
    call(
      "copy_named_output",
      ["string", "number", "number"],
      ["result.pdf", 0, 0],
    ),
    0,
  );
  if (process.env.INCLUDE === "1") {
    assert.equal(
      call("named_output_size", ["string"], ["chapters/chapter.aux"]),
      0,
    );
    assert.equal(
      call(
        "copy_named_output",
        ["string", "number", "number"],
        ["chapters/chapter.aux", 0, 0],
      ),
      0,
    );
  }
  for (const name of declaredNames) {
    assert.equal(
      call("named_output_size", ["string"], [name]),
      0,
      "reuse revokes bibliography output",
    );
    assert.equal(
      call("copy_named_output", ["string", "number", "number"], [name, 0, 0]),
      0,
    );
  }
  assert.equal(
    call(
      "add_file",
      ["number", "string", "number", "number"],
      [1, "late.tex", 0, 0],
    ),
    -2,
  );
  let budget;
  try {
    budget = JSON.parse(call("budget_json", [], [], "string"));
  } catch (error) {
    throw new Error("Invalid native budget report", { cause: error });
  }
  if (budget.installed) {
    assert.ok(
      budget.growthBytes <= 16777216 &&
        budget.backingBytes + budget.copyBytes <= 16777216,
    );
    assert.ok(
      budget.newNodes <= 128 &&
        budget.logBytes <= 262144 &&
        budget.transportBytes <= 262144,
    );
  }
  const receipt = {
    label,
    mode,
    status,
    nativeError,
    budget,
    recorderError,
    postCase,
    apiErrorRevocation: process.env.API_ERROR_AFTER_SUCCESS === "1",
    root,
    rootUtf8Hex: Buffer.from(root).toString("hex"),
    pathAliases,
    version: call("version", [], [], "string"),
    elapsedMs: Date.now() - started,
    loaderSha256,
    wasmSha256: sha(wasm),
    manifestSha256: sha(manifestBytes),
    memoryBytes: m.HEAPU8.byteLength,
    formatSha256:
      mode === "format"
        ? artifacts.fmt?.sha256
        : sha(
            fs.readFileSync(
              process.env.FORMAT_FILE || path.join(candidate, "lualatex.fmt"),
            ),
          ),
    sourceSha256:
      mode === "format"
        ? null
        : sha(fs.readFileSync(path.join(results, label + ".source.tex"))),
    carryInputs,
    artifacts,
  };
  fs.writeFileSync(
    path.join(results, label + ".console.txt"),
    lines.join("\n") + "\n",
  );
  fs.writeFileSync(
    path.join(results, label + ".json"),
    JSON.stringify(receipt, null, 2) + "\n",
  );
  console.log(JSON.stringify(receipt));
  console.log(lines.slice(-25).join("\n"));
  if (postCase) {
    assert.equal(status, 0);
    assert.equal(budget.failed, true);
    assert.ok(!artifacts.pdf && !artifacts.fmt);
  } else if (process.env.EXPECT_FAILURE === "1") {
    assert.notEqual(status, 0);
    assert.ok(!artifacts.pdf && !artifacts.fmt);
  } else {
    assert.equal(status, 0, nativeError);
    assert.ok(artifacts[mode === "format" ? "fmt" : "pdf"]);
  }
})().catch((e) => {
  fs.writeFileSync(
    path.join(results, label + ".console.txt"),
    lines.join("\n") + "\n",
  );
  console.error(e);
  process.exitCode = 1;
});
