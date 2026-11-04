"use strict";
// Native candidate test tooling only. No browser/profile adoption is implied.
const fs = require("node:fs"), path = require("node:path");
const assert = require("node:assert/strict");
const { createHash } = require("node:crypto");
const { Worker, isMainThread, parentPort, workerData } = require("node:worker_threads");
const sha = b => createHash("sha256").update(b).digest("hex");
const required = key => { assert.ok(process.env[key], "Set " + key); return process.env[key]; };
const mode = isMainThread ? process.argv[2] : workerData.mode;
assert.ok(["format", "shape", "harfbuzz", "plain", "denied", "runaway"].includes(mode), "Select one native case");
const out = path.resolve(required("RESULT_DIR"));

async function inspectArabicPdf(pdfFile) {
  const runtime = path.resolve(required("PDF_RUNTIME_DIR"));
  const loader = fs.readFileSync(path.join(runtime, "e1.js"));
  const wasm = fs.readFileSync(path.join(runtime, "e1.wasm"));
  assert.equal(sha(loader), "040bdfac50656b3993f622614b73feaaba31d8253d077ff50f925f316c2f5f6b");
  assert.equal(sha(wasm), "1637c1a7798cab443334e559045d88f33864d89e0ff02eeb57b3da4f08f37c20");
  const m = await require(path.join(runtime, "e1.js"))({ wasmBinary: wasm, print() {}, printErr() {} });
  const check = (name, ...args) => assert.equal(m["_" + name](...args), 0, name);
  const input = bytes => { const h = m._e1_alloc(bytes.length); assert.ok(h); m.HEAPU8.set(bytes, m._e1_data(h)); return h; };
  const take = () => {
    const h = m._e1_result(), n = m._e1_size(h), copy = input(Buffer.alloc(n));
    check("e1_copy", h, copy); const bytes = Buffer.from(m.HEAPU8.slice(m._e1_data(copy), m._e1_data(copy) + n));
    check("e1_release", copy); check("e1_release", h); return bytes;
  };
  const raw = Buffer.alloc(36);
  [1, 36, 8388608, 32, 100000, 256, 4000000, 8388608, 64].forEach((n, i) => raw.writeUInt32LE(n, i * 4));
  const bytes = fs.readFileSync(pdfFile), data = input(bytes), budget = input(raw);
  check("e1_open", data, budget); const doc = m._e1_result();
  check("e1_release", data); check("e1_release", budget);
  assert.equal(m._e1_document_pages(doc), 1);
  check("e1_page", doc, 0); const page = m._e1_result();
  check("e1_text", page); const text = JSON.parse(take());
  const queryText = "مرحبا بالعالم", query = input(Buffer.from(queryText + "\0", "utf16le"));
  check("e1_search", page, query); const search = JSON.parse(take()); check("e1_release", query);
  check("e1_raster", page, 612, 792); const raster = take();
  assert.equal(raster.readUInt32LE(0), 0x31424752);
  let inkPixels = 0;
  for (let i = 16; i < raster.length; i += 4) if (Math.min(raster[i], raster[i + 1], raster[i + 2]) < 240) inkPixels++;
  check("e1_close", page); check("e1_close", doc); assert.equal(m._e1_live(), 0);
  const result = { scope: "actual accepted E1 extraction/search/raster, independent of source-derived text",
    pdfSha256: sha(bytes), loaderSha256: sha(loader), wasmSha256: sha(wasm), ...text, queryText, search,
    raster: { length: raster.length, sha256: sha(raster), inkPixels } };
  fs.writeFileSync(path.join(out, "pdf-inspection.json"), JSON.stringify(result, null, 2) + "\n");
  assert.ok(text.text.includes(queryText), "accepted renderer must extract the original Arabic phrase, including space");
  assert.ok(search.length > 0, "literal original Arabic search must find actual PDF text");
  assert.ok(inkPixels > 100, "actual nonblank raster required");
  return { queryText, search, raster: result.raster, text: text.text };
}

async function attempt() {
  const baseline = process.env.P10_BASELINE === "1";
  assert.ok(!baseline || mode === "denied", "P10 comparison is only a denial control, never a shaping pass");
  const runtime = path.resolve(required("P10_RUNTIME_DIR"));
  const candidate = baseline ? path.join(runtime, "files") : path.resolve(required("LUA_CANDIDATE_DIR"));
  const upstream = path.resolve(required("LUAHARFBUZZ_SOURCE_DIR"));
  const pinsBytes = fs.readFileSync(path.join(runtime, "pins.json"));
  assert.equal(sha(pinsBytes), "3dcca8f206211dbe0d3346b54f8fef4f5b7e7be904a241f3b6f3376e8d948289");
  const pins = JSON.parse(pinsBytes);
  assert.equal(pins.length, 3709);
  const manifestBytes = baseline ? pinsBytes : fs.readFileSync(path.join(candidate, "harfbuzz-build.json"));
  if (!baseline) assert.equal(sha(manifestBytes), required("CANDIDATE_MANIFEST_SHA256"));
  const build = baseline ? { outputs: Object.fromEntries(pins.map(p => [p.name, p])) } : JSON.parse(manifestBytes);
  if (!baseline) { assert.equal(build.status, "candidate-not-adopted"); assert.equal(build.nativeAbi, 5); }
  const inputs = [];
  function readPinned(base, name, pin) {
    const bytes = fs.readFileSync(path.join(base, name));
    assert.equal(bytes.length, pin.length, name);
    assert.equal(sha(bytes), pin.sha256, name);
    inputs.push({ name, ...pin });
    return bytes;
  }
  const loader = readPinned(candidate, "luatex.js", build.outputs["luatex.js"]);
  const wasm = readPinned(candidate, "luatex.wasm", build.outputs["luatex.wasm"]);
  const sourcePins = JSON.parse(fs.readFileSync(path.join(__dirname, "fixtures/upstream-pins.json")));
  const sourceBytes = Object.fromEntries(Object.entries(sourcePins).map(([name, pin]) =>
    [name, readPinned(upstream, name, pin)]));
  const log = [];
  const print = line => {
    log.push(line);
    if (line.includes("LUAHB-CANCEL-BEGIN")) parentPort.postMessage({ enteredActualLuaLoop: true });
  };
  const create = require(path.join(candidate, "luatex.js"));
  const module = await create({ noInitialRun: true, print, printErr: print,
    instantiateWasm(imports, success) {
      const instance = new WebAssembly.Instance(new WebAssembly.Module(wasm), imports);
      success(instance); return instance.exports;
    } });
  const call = (name, types = [], args = [], result = "number") =>
    module.ccall("oc_luatex_" + name, result, types, args);
  const error = () => call("error", [], [], "string");
  const version = call("version", [], [], "string");
  assert.equal(call("abi"), 5);
  if (baseline) assert.equal(typeof module._oc_lua_harfbuzz_abi, "undefined");
  else assert.equal(module._oc_lua_harfbuzz_abi(), 1);
  assert.equal(module._oc_lua_graphics_abi(), 1);
  const selectedRoot = "тези/розділ 😀.tex";
  assert.equal(call("configure", ["string", "string"], [selectedRoot, "luahb-native-candidate"]), 0, error());
  let seededBytes = 0;
  const seed = (kind, name, bytes) => {
    const pointer = module._malloc(bytes.length || 1);
    module.HEAPU8.set(bytes, pointer);
    const status = call("add_file", ["number", "string", "number", "number"], [kind, name, pointer, bytes.length]);
    module._free(pointer);
    assert.equal(status, 0, name + ": " + error());
    seededBytes += bytes.length;
    if (kind !== 0) inputs.push({ role: kind, name, length: bytes.length, sha256: sha(bytes) });
  };
  for (const pin of pins) {
    if (/\.(js|wasm|fmt)$/.test(pin.name) || pin.name.startsWith("notices/") ||
        ["icudt68l.dat", "xetexfontlist.txt"].includes(pin.name)) continue;
    seed(0, pin.name, readPinned(path.join(runtime, "files"), pin.name, pin));
  }
  if (mode !== "format") {
    assert.ok(!baseline || !process.env.FORMAT_FILE, "P10 control must retain its own exact format");
    const format = pins.find(p => p.name === "lualatex.fmt");
    const bytes = process.env.FORMAT_FILE ? fs.readFileSync(process.env.FORMAT_FILE) :
      readPinned(path.join(runtime, "files"), "lualatex.fmt", format);
    if (process.env.FORMAT_FILE) {
      assert.equal(sha(bytes), required("FORMAT_SHA256"));
      inputs.push({ name: "candidate-lualatex.fmt", length: bytes.length, sha256: sha(bytes) });
    }
    seed(0, "lualatex.fmt", bytes);
  }
  seed(0, "harfbuzz.lua", sourceBytes["src/harfbuzz.lua"]);
  seed(1, "fonts/notonastaliq.ttf", sourceBytes["fonts/notonastaliq.ttf"]);
  seed(1, "fonts/amiri-regular.ttf", sourceBytes["fonts/amiri-regular.ttf"]);
  const fixture = name => fs.readFileSync(path.join(__dirname, "fixtures", name));
  const original = mode === "format" ? Buffer.from("% format attempt has no source entry\n") : fixture(mode + ".tex");
  seed(1, selectedRoot, original);
  if (mode === "shape" || mode === "denied") seed(1, mode + ".lua", fixture(mode + ".lua"));
  const started = performance.now();
  const status = call(mode === "format" ? "compile_format" : "compile_latex");
  const compileMs = performance.now() - started;
  fs.writeFileSync(path.join(out, "native-console.txt"), log.join("\n") + "\n");
  function artifact(id, name) {
    const size = call("output_size", ["number"], [id]);
    if (!size) return null;
    assert.ok(size > 0 && size <= 16 * 1024 * 1024);
    const pointer = module._malloc(size);
    assert.equal(call("copy_output", ["number", "number", "number"], [id, pointer, size]), size);
    const bytes = Buffer.from(module.HEAPU8.slice(pointer, pointer + size));
    module._free(pointer); fs.writeFileSync(path.join(out, name), bytes);
    return { length: size, sha256: sha(bytes) };
  }
  const pdf = artifact(0, "output.pdf"), nativeLog = artifact(1, "output.log");
  const generatedFormat = mode === "format" ? artifact(2, "lualatex.fmt") : null;
  const receipt = { schemaVersion: 1, scope: "native candidate only; no application adoption/browser acceptance",
    testScriptSha256: sha(fs.readFileSync(__filename)),
    mode, baseline, status, nativeError: error(), version, seededBytes, compileMs,
    candidate: {"luatex.js": build.outputs["luatex.js"], "luatex.wasm": build.outputs["luatex.wasm"]},
    manifestSha256: sha(manifestBytes), selectedRoot, sourceSha256: sha(original), pdf, nativeLog, generatedFormat, inputs };
  fs.writeFileSync(path.join(out, "native-attempt.json"), JSON.stringify(receipt, null, 2) + "\n");
  assert.equal(status, 0, error() + "\n" + log.slice(-18).join("\n"));
  assert.match(version, baseline ? /LuaTeX 1\.13\.0/ : /LuaHBTeX 1\.13\.0/);
  assert.match(log.join("\n"), baseline ? /This is LuaTeX, Version 1\.13\.0/ : /This is LuaHBTeX, Version 1\.13\.0/);
  if (mode === "format") assert.ok(generatedFormat?.length > 1000000, "genuine fresh LuaHBTeX format required");
  else assert.ok(pdf?.length > 1000, "actual fresh native PDF required");
  assert.deepEqual(Buffer.from(module.FS.readFile("/work/project/" + selectedRoot)), original);
  if (mode === "shape") {
    const text = log.join("\n");
    assert.match(text, /LUAHBVERSION=2\.7\.4/);
    for (const [id, file] of [["urdu", "notonastaliq_U06CC_U06C1.json"],
      ["digits", "amiri-regular_123.json"], ["numerators", "amiri-regular_123_numr.json"]]) {
      const expected = JSON.parse(sourceBytes["fixtures/" + file]);
      const actual = [...text.matchAll(new RegExp("LUAHBSHAPE=" + id + ":(\\d+):([-\\d.,]+)", "g"))]
        .map(m => {
          const values = m[2].split(",").map(Number);
          assert.equal(values.length, 7, "capture every emitted glyph field");
          assert.ok(values.every(Number.isSafeInteger), "native glyph metrics must be exact integers");
          const [g, cl, ax, ay, dx, dy, flags] = values;
          const glyph = {g, cl, ax, ay, dx, dy};
          // This upstream binding/fixture omits zero flags. Preserve that exact
          // optional field rather than dropping or inventing numeric metrics.
          if (flags !== -1) glyph.fl = flags;
          return glyph;
        });
      assert.deepEqual(actual, expected, "exact upstream hb-shape oracle: " + id);
    }
    receipt.exactUpstreamShapeVectors = 3;
  }
  if (mode === "denied") assert.match(log.join("\n"), /LUAHB-DENIALS-PASS/);
  if (mode === "harfbuzz") receipt.renderer = await inspectArabicPdf(path.join(out, "output.pdf"));
  receipt.passed = true;
  fs.writeFileSync(path.join(out, "receipt.json"), JSON.stringify(receipt, null, 2) + "\n");
  return { mode, passed: true, compileMs, seededBytes, pdf, version };
}

if (isMainThread) {
  assert.ok(fs.existsSync(out) && fs.readdirSync(out).length === 0, "fresh empty RESULT_DIR required");
  const worker = new Worker(__filename, { workerData: { mode }, resourceLimits: { maxOldGenerationSizeMb: 512 } });
  let entered = false, killedAt = null, completed = false;
  const timeout = setTimeout(() => { void worker.terminate(); }, 45000);
  worker.on("message", message => {
    if (message.enteredActualLuaLoop && mode === "runaway") {
      entered = true;
      setTimeout(() => { killedAt = performance.now(); void worker.terminate(); }, 250);
    } else if (message.result) { completed = true; console.log(JSON.stringify(message.result)); }
  });
  worker.on("error", error => { clearTimeout(timeout); console.error(error); process.exitCode = 1; });
  worker.on("exit", code => {
    clearTimeout(timeout);
    if (mode === "runaway") {
      assert.ok(entered && killedAt !== null, "native Lua loop must precede physical cancellation");
      assert.equal(code, 1);
      assert.ok(!fs.existsSync(path.join(out, "output.pdf")));
      const receipt = { mode, passed: true, actualLuaLoopEntered: true, physicalWorkerTermination: true,
        testScriptSha256: sha(fs.readFileSync(__filename)), candidateManifestSha256: required("CANDIDATE_MANIFEST_SHA256"),
        terminationMs: performance.now() - killedAt, scope: "native worker test; not browser isolation acceptance" };
      fs.writeFileSync(path.join(out, "receipt.json"), JSON.stringify(receipt, null, 2) + "\n");
      console.log(JSON.stringify(receipt));
    } else if (code !== 0 || !completed) process.exitCode = 1;
  });
} else attempt().then(result => parentPort.postMessage({ result })).catch(error => { throw error; });
