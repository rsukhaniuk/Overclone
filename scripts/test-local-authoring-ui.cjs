// Real published-app compile/PDF/cold-offline acceptance. Lead owns the cached
// untrimmed SDK publication and the actual browser run; this harness only starts
// an owned static origin over PUBLISHED_WWWROOT, drives the real UI through the
// application's registered production C# code, and records a sanitized factual
// receipt. No product code is changed here. No injected runtime provider,
// worker, compiler output, renderer result, metadata approval, native response
// or browser flag/CSP bypass: actual source edit/save/analyze and native
// compile/render through the registered production code is the point.
//
// Required env:
//   PUBLISHED_WWWROOT      published static Blazor WASM build (lead-staged)
//   AUTHORING_RUNTIME_DIR  supported 497/499/507 pins.json + files/
//   PDF_RUNTIME_DIR        trusted E1 manifest.json + files/ (loader,wasm)
// Optional:
//   RESULT_DIR             writable receipt directory (lead-supplied)
//   CASE                    plain (default) | golden | long | font-assets | portable | packages | biblatex | packages-compiler | biblatex-compiler | biber | cyrillic-semantics | harfbuzz | t2a-bundled | font-closure | unicode-fonts
//   COMPILER_CHOICE         local-authoring-v4 (default) | latex-dvi | xelatex | lualatex
//   T2A_FONT_PROJECT_DIR    required only for font-assets: exact 7 private project files
//   RESULT_DIR             required for font-assets/final R1 cases: retain actual PDFs
//   PREFLIGHT_ONLY=1        fixture checks only; exits BEFORE origin/browser creation
//   PDF_DOWNLOAD_CHECKS=1   optional CASE=plain online/offline/stale export checks (RESULT_DIR required)
//   SOURCE_PDF_CHECKS=1     plain/golden/portable/biber native forward/inverse checks
//   RENDERED_REVIEW_CHECKS=1 plain/portable P14+layout-renderer checks; bundled and PDF_DOWNLOAD_CHECKS=1 required
//   WORK_PAGE_NUMBER_CHECKS=1 plain P16 pdfLaTeX fixture with explicit title and work page-number labels
//   WORK_MAJOR_HEADING_CHECKS=1 plain P16 pdfLaTeX fixture with a separate three-line heading-order page
//   BUNDLED_RUNTIME=1       mandatory for final R1 cases; automatic embedded tools
//   CYRILLIC_FIXTURE        mixed-ligature.tex (default) | mixed-semantic.tex, both original unchanged
//   LUAHARFBUZZ_SOURCE_DIR   approved native LuaHarfbuzz source root, required only for harfbuzz
//   T2A_UNICODE_REPAIR=1    explicit source-example opt-in, ONLY CASE=font-assets; original inputs still verified first
// packages/biblatex always require RESULT_DIR, exact PDF downloads and native
// root/include navigation. Supply the complete mapped P10 runtime for all engines.
// packages-compiler/biblatex-compiler use those EXACT fixtures and all compiler,
// text, final-pass, download and cold checks; native navigation is NOT ASSESSED
// pending R2. SOURCE_PDF_CHECKS=1 is refused for these explicitly scoped cases.
// Their URI links/outlines need separate inspection of the retained actual PDFs:
// the registered preview does not request/expose passive actions or bookmarks.
//
// plain  : single main.tex, no BibTeX — full compile + render + cold offline.
// golden : exact accepted fixture bytes extracted from the controlled repo
//          file tests/ModernCompilerBrowserHarness/AuthoringFixture.cs (not a
//          hand-written variant) with BibTeX — Ukrainian + bibliography +
//          cross-references — full compile + render + cold offline.
// long   : unchanged 64-file TOC fixed-point fixture; the supported 512-page
//          renderer must render first/last pages and explicitly continue search in batches of32.
// font-assets: exact user-supplied 10pt T2A/cmr regular+bold project assets;
//          real compile/render/download and full cold restart. No new runtime profile.
//          Font programs/metrics remain private inputs, never shipped by this script.

const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const http = require("node:http");
const { createHash } = require("node:crypto");
const { chromium } = require("playwright");
const H = require("./review-ui-helpers.cjs");
const sourcePdfNavigation = require("./source-pdf-navigation-ui-checks.cjs");
const renderedReview = require("./rendered-review-ui-checks.cjs");
const workPageNumbers = require("./work-page-number-ui-checks.cjs");
const { validate: validateCsp } = require("./test-csp.cjs");

const sha256 = (b) => createHash("sha256").update(b).digest("hex");
const req = (n) => {
  assert.ok(process.env[n], `${n} must be explicitly supplied`);
  return process.env[n];
};
const parseUrl = (v) => {
  try { return new URL(v); } catch { throw new Error("Expected a valid URL"); }
};
const json = (v) => { try { return JSON.parse(v); } catch { throw new Error("Expected valid JSON"); } };

const PREFLIGHT_ONLY = process.env.PREFLIGHT_ONLY === "1";
const WWWROOT = PREFLIGHT_ONLY ? "" : req("PUBLISHED_WWWROOT");
const RUNTIME_DIR = req("AUTHORING_RUNTIME_DIR");
const PDF_DIR = req("PDF_RUNTIME_DIR");
const RESULT_DIR = process.env.RESULT_DIR || "";
const CASE = process.env.CASE || "plain";
const STRICT_PACKAGE_CASE = ["packages", "biblatex"].includes(CASE);
const PACKAGE_COMPILER_ONLY = ["packages-compiler", "biblatex-compiler"].includes(CASE);
const FIXTURE_CASE = PACKAGE_COMPILER_ONLY ? CASE.slice(0, -"-compiler".length) : CASE;
const PACKAGE_CASE = STRICT_PACKAGE_CASE || PACKAGE_COMPILER_ONLY;
const BIBER_CASE = CASE === 'biber';
const CYRILLIC_CASE = CASE === 'cyrillic-semantics';
const HARFBUZZ_CASE = CASE === 'harfbuzz';
const FINAL_R1_CASE = BIBER_CASE || CYRILLIC_CASE || HARFBUZZ_CASE;
const COMPILER_CHOICE = process.env.COMPILER_CHOICE || "local-authoring-v4";
assert.ok(["local-authoring-v4", "latex-dvi", "xelatex", "lualatex"].includes(COMPILER_CHOICE), "explicit supported compiler choice");
const BUNDLED_RUNTIME = process.env.BUNDLED_RUNTIME === "1";
assert.ok(!FINAL_R1_CASE || BUNDLED_RUNTIME, 'final R1 cases require automatic bundled tools');
const SOURCE_PDF_CHECKS = process.env.SOURCE_PDF_CHECKS === "1" || STRICT_PACKAGE_CASE;
const RENDERED_REVIEW_CHECKS = renderedReview.enabled(process.env.RENDERED_REVIEW_CHECKS);
const WORK_PAGE_NUMBER_CHECKS = renderedReview.enabled(process.env.WORK_PAGE_NUMBER_CHECKS);
const WORK_MAJOR_HEADING_CHECKS = renderedReview.enabled(process.env.WORK_MAJOR_HEADING_CHECKS);
const workMajorHeading = WORK_MAJOR_HEADING_CHECKS ? require("./work-major-heading-ui-checks.cjs") : null;
assert.ok(!SOURCE_PDF_CHECKS || ["plain", "golden", "portable", "packages", "biblatex", "biber"].includes(CASE), "source/PDF checks support plain/golden/portable/packages/biblatex/biber fixtures");
// Opt-in real UI rename of the original input bytes, followed by the same
// registered compile/BibTeX/render/full-cold-offline workflow.
const MAIN_PATH = process.env.SELECTED_MAIN_PATH || "main.tex";
assert.ok(MAIN_PATH === "main.tex" || ["plain", "golden", "portable", "packages", "biblatex", "t2a-bundled", "font-closure", "unicode-fonts", "biber", "cyrillic-semantics", "harfbuzz"].includes(FIXTURE_CASE), "selected root supports plain/golden/portable/packages/biblatex/t2a-bundled fixtures");
assert.ok(MAIN_PATH.length <= 200 && MAIN_PATH.endsWith(".tex") && !MAIN_PATH.split("/").some(n => !n || n === "." || n === ".."));
const PORT = 8768; // dedicated container-only origin port
assert.ok(["plain", "golden", "long", "font-assets", "portable", "packages", "biblatex", "packages-compiler", "biblatex-compiler", "t2a-bundled", "font-closure", "unicode-fonts", "biber", "cyrillic-semantics", "harfbuzz"].includes(CASE), "unsupported authoring CASE");
assert.ok(CASE !== 't2a-bundled' || ['local-authoring-v4', 'latex-dvi'].includes(COMPILER_CHOICE), 'bundled T2A fixture targets pdfTeX-based engines');
const PDF_DOWNLOAD_CHECKS = process.env.PDF_DOWNLOAD_CHECKS === "1";
assert.ok(!PDF_DOWNLOAD_CHECKS || ["plain", "portable", "packages", "biblatex", "t2a-bundled", "font-closure", "unicode-fonts", "biber", "cyrillic-semantics", "harfbuzz"].includes(FIXTURE_CASE), "PDF_DOWNLOAD_CHECKS=1 requires plain/portable/packages/biblatex/t2a-bundled");
const repairSetting = process.env.T2A_UNICODE_REPAIR;
assert.ok(repairSetting === undefined || ["", "0", "1"].includes(repairSetting), "T2A_UNICODE_REPAIR must be absent,0 or1");
const T2A_UNICODE_REPAIR = repairSetting === "1";
assert.ok(!T2A_UNICODE_REPAIR || CASE === "font-assets", "T2A_UNICODE_REPAIR=1 is supported only for CASE=font-assets");
const CHECK_DOWNLOADS = CASE === "font-assets" || PDF_DOWNLOAD_CHECKS || PACKAGE_CASE || FINAL_R1_CASE;
assert.ok(!WORK_PAGE_NUMBER_CHECKS || CASE === 'plain' && COMPILER_CHOICE === 'local-authoring-v4'
  && BUNDLED_RUNTIME && PDF_DOWNLOAD_CHECKS && !RENDERED_REVIEW_CHECKS && !SOURCE_PDF_CHECKS,
  'work page-number workflow uses its explicit plain, bundled pdfLaTeX case');
assert.ok(!WORK_MAJOR_HEADING_CHECKS || CASE === 'plain' && COMPILER_CHOICE === 'local-authoring-v4'
  && BUNDLED_RUNTIME && PDF_DOWNLOAD_CHECKS && !WORK_PAGE_NUMBER_CHECKS && !RENDERED_REVIEW_CHECKS && !SOURCE_PDF_CHECKS,
  'major heading order uses its explicit plain, bundled pdfLaTeX case');
if (CHECK_DOWNLOADS) assert.ok(RESULT_DIR, "RESULT_DIR is required to retain actual PDF downloads");

// --- Validate the published SDK manifest: EVERY asset byte hash, as in the
// accepted test-native-worker-csp-ui.cjs (not just a few file existence checks).
const cspHeader = fs.readFileSync(path.join(__dirname, "../docs/examples/static-csp-headers.conf"), "utf8");
let manifest;
if (!PREFLIGHT_ONLY) {
const indexPath = path.join(WWWROOT, "index.html");
const indexHtml = fs.readFileSync(indexPath, "utf8");
validateCsp(indexHtml, cspHeader);
const manifestText = fs.readFileSync(path.join(WWWROOT, "service-worker-assets.js"), "utf8");
try {
  manifest = JSON.parse(manifestText.match(/self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;/)[1]);
} catch {
  throw new Error("published SDK manifest must be valid generated JSON");
}
for (const asset of manifest.assets) {
  assert.ok(!asset.url.startsWith("/") && !asset.url.split("/").includes(".."), `asset path: ${asset.url}`);
  assert.equal(
    "sha256-" + createHash("sha256").update(fs.readFileSync(path.join(WWWROOT, asset.url))).digest("base64"),
    asset.hash,
    `SDK asset hash mismatch: ${asset.url}`,
  );
}
}

// --- 497 compiler resource pins (Node runner only; never UI-inject storage).
const pinsPath = path.join(RUNTIME_DIR, "pins.json");
const pins = json(fs.readFileSync(pinsPath, "utf8"));
const suite = pins.some(pin => pin.name === 'xetex.wasm') && pins.some(pin => pin.name === 'luatex.wasm');
assert.ok([497, 499, 507].includes(pins.length) || suite && pins.length <= 8192, "supported authoring resource inventory");
if (SOURCE_PDF_CHECKS) assert.ok([499, 507].includes(pins.length) || suite, "navigation requires source-map resources");
if (SOURCE_PDF_CHECKS && COMPILER_CHOICE !== "local-authoring-v4")
  assert.ok(pins.some(pin => pin.name === 'xdv-map-converter.wasm'), "non-pdfTeX navigation requires the complete P9 bundle");
if (COMPILER_CHOICE === "latex-dvi") assert.ok(pins.length === 507 || suite, "DVI requires its resource package");
if (["xelatex", "lualatex"].includes(COMPILER_CHOICE)) assert.ok(suite, "Unicode engines require the explicit suite");
for (const p of pins) {
  assert.match(p.name, suite ? /^(?=.{1,200}$)[A-Za-z0-9._-]+(?:\/[A-Za-z0-9._-]+)*$/ : /^[A-Za-z0-9._-]{1,200}$/, "bounded pin name");
  assert.ok(!p.name.split('/').some(part => part === '.' || part === '..'), "bounded pin path");
  assert.ok(Number.isFinite(p.length) && p.length >= 0, "pin length");
  assert.match(p.sha256, /^[0-9a-f]{64}$/, "pin sha256");
}
const compilerFiles = pins.map((p) => path.join(RUNTIME_DIR, "files", p.name));
for (const f of compilerFiles) assert.ok(fs.existsSync(f), `pin file missing: ${path.basename(f)}`);
const biberDirectory = path.join(__dirname, '../tests/CompilerBiberFixtures');
const biberManifest = BIBER_CASE ? json(fs.readFileSync(path.join(biberDirectory, 'manifest.json'), 'utf8')) : null;
const portableFiles = BIBER_CASE ? (() => {
  assert.equal(biberManifest.backend, 'biber'); assert.equal(biberManifest.biblatex, '3.19'); assert.equal(biberManifest.biber, '2.19');
  for (const name of ['biber.js', 'biber.wasm', 'biber.data']) assert.ok(pins.some(p => p.name === name), 'genuine bundled Biber input '+name);
  const main = ['xelatex','lualatex'].includes(COMPILER_CHOICE) ? 'main-unicode.tex' : 'main-latin.tex';
  const entries = [main, 'chapter.tex', 'references-latin.bib', 'references-ukrainian.bib'].map(source => {
    const b=fs.readFileSync(path.join(biberDirectory,source)), expected=biberManifest.files.find(f=>f.name===source);
    assert.ok(expected); assert.equal(b.length,expected.length); assert.equal(sha256(b),expected.sha256);
    return {name:source===main?'main.tex':source,buffer:b,mimeType:'text/plain'};
  });
  assert.match(entries[0].buffer.toString(), /backend=biber/);
  entries.push({name:'figure.xbb',buffer:fs.readFileSync(path.join(__dirname,'../tests/CompilerSuiteFixtures/figure.xbb')),mimeType:'text/plain'});
  entries.push({name:'figure.png',buffer:fs.readFileSync(path.join(__dirname,'../src/Client/wwwroot/icons/icon-192.png')),mimeType:'image/png'});
  return entries;
})() : ['portable', 'packages', 'biblatex'].includes(FIXTURE_CASE) ? (() => {
  const directory = path.join(__dirname, '../tests/CompilerSuiteFixtures');
  const mainDirectory = FIXTURE_CASE === 'biblatex' ? path.join(__dirname, '../tests/CompilerBibliographyFixtures')
    : FIXTURE_CASE === 'packages' ? path.join(__dirname, '../tests/PackageFontFixtures') : directory;
  const unicode = ['xelatex', 'lualatex'].includes(COMPILER_CHOICE);
  const entries = [['main.tex', unicode ? 'main-unicode.tex' : 'main-latin.tex'],
    ['chapter.tex', 'chapter.tex'], ['references.bib', 'references.bib'], ['figure.xbb', 'figure.xbb']]
    .map(([name, source]) => ({name, buffer: fs.readFileSync(path.join(name === 'main.tex' ? mainDirectory : directory, source)), mimeType: 'text/plain'}));
  entries.push({name: 'figure.png', buffer: fs.readFileSync(path.join(__dirname, '../src/Client/wwwroot/icons/icon-192.png')), mimeType: 'image/png'});
  return entries;
})() : null;
const BIBER_COLD_EDIT = {
  file: 'references-latin.bib', key: 'offline-added-book', marker: 'Biber Cold Offline Added Book Marker',
  citation: 'Offline added citation~\\cite{offline-added-book}.', renderedCitation: 'Offline added citation [4].',
  entry: '\n@book{offline-added-book,\n  author = {Baker, Alice},\n  title = {{Biber Cold Offline Added Book Marker}},\n  publisher = {Offline Revision Press},\n  date = {2027}\n}\n',
};
const changedBiberBib = BIBER_CASE ? (()=>{
  const original=portableFiles.find(f=>f.name===BIBER_COLD_EDIT.file).buffer;
  assert.ok(!portableFiles.filter(f=>f.name.endsWith('.bib')).some(f=>f.buffer.toString().includes(BIBER_COLD_EDIT.key)), 'new offline citekey is absent from original inputs');
  const bytes=Buffer.concat([original,Buffer.from(BIBER_COLD_EDIT.entry,'utf8')]);
  assert.deepEqual(bytes.subarray(0,original.length),original,'BIB edit appends without changing original entries');
  const keys=[...Buffer.concat([bytes,portableFiles.find(f=>f.name==='references-ukrainian.bib').buffer]).toString().matchAll(/@(?:book|article)\s*\{([^,]+),/g)].map(m=>m[1]);
  assert.deepEqual(keys.slice().sort(),[...biberManifest.citationKeys,BIBER_COLD_EDIT.key].sort(),'cold edit defines exactly four original/new bibliography entries');
  return bytes;
})() : null;
const packageCompilerScope = () => !PACKAGE_COMPILER_ONLY ? null : ({
  fixtureCase:FIXTURE_CASE, scope:'compiler/package/font regression with complete cold-offline workflow',
  nativeNavigation:{status:'not-assessed',pendingMilestone:'R2',reason:'Explicit compiler-only case; use the unchanged packages/biblatex cases for strict root/include navigation.'},
  fixtures:portableFiles.map(f=>({name:f.name,length:f.buffer.length,sha256:sha256(f.buffer)})),
});
const semanticDirectory = path.join(__dirname, '../tests/CompilerSemanticsFixtures');
const semanticsManifest = CYRILLIC_CASE || HARFBUZZ_CASE ? json(fs.readFileSync(path.join(semanticDirectory,'manifest.json'),'utf8')) : null;
const CYRILLIC_FIXTURE = process.env.CYRILLIC_FIXTURE || 'mixed-ligature.tex';
assert.ok(!CYRILLIC_CASE || ['mixed-semantic.tex','mixed-ligature.tex'].includes(CYRILLIC_FIXTURE),'exact original Cyrillic fixture');
assert.ok(!CYRILLIC_CASE || ['local-authoring-v4','latex-dvi'].includes(COMPILER_CHOICE),'original T2A semantics requires pdfTeX/DVI');
assert.ok(!HARFBUZZ_CASE || COMPILER_CHOICE==='lualatex','HarfBuzz fixture requires the genuine LuaHB engine');
const semanticFiles = CYRILLIC_CASE || HARFBUZZ_CASE ? (() => {
  const selected=HARFBUZZ_CASE?'harfbuzz.tex':CYRILLIC_FIXTURE, expected=semanticsManifest.files.find(f=>f.name===selected);
  const buffer=fs.readFileSync(path.join(__dirname,'..',expected.source));
  assert.equal(buffer.length,expected.length);assert.equal(sha256(buffer),expected.sha256);
  const files=[{name:'main.tex',buffer,mimeType:'text/plain'}];
  if(HARFBUZZ_CASE){
    assert.match(buffer.toString(),/Renderer=HarfBuzz/);
    const font=semanticsManifest.amiri,bytes=fs.readFileSync(path.join(req('LUAHARFBUZZ_SOURCE_DIR'),font.source));
    assert.equal(bytes.length,font.length);assert.equal(sha256(bytes),font.sha256);
    files.push({name:'fonts/amiri-regular.ttf',buffer:bytes,mimeType:'font/ttf'});
  }
  return files;
})() : null;
const capturedR1Files = BIBER_CASE ? portableFiles : semanticFiles;
const r1Preflight = () => !FINAL_R1_CASE ? null : ({case:CASE,browserNativeProof:false,
  originalSource:!BIBER_CASE,selectedFixture:BIBER_CASE?(['xelatex','lualatex'].includes(COMPILER_CHOICE)?'main-unicode.tex':'main-latin.tex'):HARFBUZZ_CASE?'harfbuzz.tex':CYRILLIC_FIXTURE,
  inputs:capturedR1Files.map(f=>({name:f.name,length:f.buffer.length,sha256:sha256(f.buffer)})),
  bibliography:BIBER_CASE?{tool:'Biber',version:'2.19',phase:10,keys:biberManifest.citationKeys,markers:biberManifest.bibliographyMarkers,
    coldOfflineEdit:{file:BIBER_COLD_EDIT.file,key:BIBER_COLD_EDIT.key,marker:BIBER_COLD_EDIT.marker,expectedEntries:4,
      changedBib:{length:changedBiberBib.length,sha256:sha256(changedBiberBib)},citation:BIBER_COLD_EDIT.citation,
      exactSavedBytesRequired:true,sameInstalledRuntimeRequired:true,backendControlFingerprintExposed:false}}:null,
  literalQueries:BIBER_CASE?biberManifest.bibliographyMarkers:semanticsManifest.queries[HARFBUZZ_CASE?'harfbuzz.tex':CYRILLIC_FIXTURE],
  nativeInputs:pins.filter(p=>BIBER_CASE?/^biber\.(js|wasm|data)$/.test(p.name):HARFBUZZ_CASE?['luatex.js','luatex.wasm','luaotfload-harf-plug.lua'].includes(p.name):['semantic-engine.js','semantic-engine.wasm'].includes(p.name))});
const FONT_CLOSURE = CASE === 'font-closure';
const UNICODE_FONTS = CASE === 'unicode-fonts';
assert.ok(!UNICODE_FONTS || ['xelatex','lualatex'].includes(COMPILER_CHOICE), 'Unicode font control requires a Unicode engine');
const selectedFontFixture = CASE === 't2a-bundled' ? 't2a-bundled.tex' : FONT_CLOSURE
  ? 'fonts/' + (['xelatex','lualatex'].includes(COMPILER_CHOICE) ? 'declared-font-commands.tex' : 'declared-faces.tex')
  : UNICODE_FONTS ? 'fonts/unicode-fontspec.tex' : null;
const bundledT2a = selectedFontFixture ? fs.readFileSync(path.join(__dirname, '../tests/PackageFontFixtures', selectedFontFixture)) : null;
function assertFontClosure(log, text) {
  assert.doesNotMatch(log, /Missing character:|LaTeX Font Warning:|not loadable|File[^\n]*not found/);
  const norm = text.replace(/\s+/g,' ').trim();
  if (FONT_CLOSURE) {
    const expected = [...bundledT2a.toString('utf8').matchAll(/\\Face\{[^}]+\}\{[^}]+\}\{[^}]+\}\{[^}]+\}\{([^}]+)\}/g)].map(m=>m[1]);
    assert.equal(expected.length,292);
    const actual = [...log.matchAll(/^R1-FONT:([^:\r\n]+):([^\r\n]+)/gm)];
    assert.ok(actual.length>=292 && actual.length%292===0, 'complete font witnesses in every genuine TeX pass');
    for(let i=0;i<actual.length;i+=292)assert.deepEqual(actual.slice(i,i+292).map(m=>m[1]), expected, 'all292 declared face selections in each pass');
    for(let i=0;i<actual.length;i++) assert.ok(actual[i][2]===expected[i%292] || actual[i][2].startsWith(expected[i%292]+' at '), 'genuine selected metric '+expected[i%292]);
    assert.equal(norm.split('Київ').length-1,292,'all292 actual page-text samples');
    return {fixture:selectedFontFixture,sourceSha256:sha256(bundledT2a),directFaces:292,projectFontFiles:0,
      scope:'declared font resources; original T2A Cyrillic I/i identity is assessed separately'};
  }
  const actual = [...log.matchAll(/^R1-UNICODE-FONT:([^\r\n]+)/gm)].map(m=>m[1]);
  assert.ok(actual.length>=16 && actual.length%16===0, 'complete Unicode style witnesses in every genuine TeX pass');
  const faces=['DejaVuSerif.ttf','DejaVuSerif-Bold.ttf','DejaVuSerif-Italic.ttf','DejaVuSerif-BoldItalic.ttf'];
  for(let i=0;i<actual.length;i++)assert.ok(actual[i].includes(faces[i%4]),'genuine Unicode style '+faces[i%4]);
  assert.equal(norm.split('Привіт').length-1,16);assert.equal(norm.split('Іі').length-1,16);
  assert.match(norm,/ASCII I i/);
  return {fixture:selectedFontFixture,sourceSha256:sha256(bundledT2a),directFaces:16,projectFontFiles:0};
}

// --- PDF resource manifest: verify actual bytes against the supplied manifest.
// The application independently pins trusted bytes; this harness does NOT
// hardcode the old E1 WASM digest. The lead supplies the fixture matching the
// reviewed app pin when running. A new 512-capable pair is not assumed here.
const pdfManifestPath = path.join(PDF_DIR, "manifest.json");
const pdfManifest = json(fs.readFileSync(pdfManifestPath, "utf8"));
assert.equal(pdfManifest.version, 1, "PDF manifest version 1");
assert.ok(Array.isArray(pdfManifest.resources) && pdfManifest.resources.length === 2, "two PDF resources");
const loaderResource = pdfManifest.resources.find((r) => r.role === "loader");
const wasmResource = pdfManifest.resources.find((r) => r.role === "wasm");
assert.ok(loaderResource && wasmResource, "loader + wasm roles");
const pdfFiles = pdfManifest.resources.map((r) => {
  assert.match(r.name, /^[A-Za-z0-9._-]{1,200}$/, "bounded pdf resource name");
  assert.ok(Number.isFinite(r.length) && r.length > 0, "pdf resource length");
  assert.match(r.sha256, /^[0-9a-f]{64}$/, "pdf resource sha256");
  const fp = path.join(PDF_DIR, "files", r.name);
  const bytes = fs.readFileSync(fp);
  assert.equal(bytes.length, r.length, `pdf byte length: ${r.name}`);
  assert.equal(sha256(bytes), r.sha256, `pdf byte hash: ${r.name}`);
  return fp;
});
const renderedReviewPreflight = RENDERED_REVIEW_CHECKS || WORK_MAJOR_HEADING_CHECKS ? renderedReview.preflight({caseName:CASE,bundled:BUNDLED_RUNTIME,
  downloads:CHECK_DOWNLOADS,resultDirectory:RESULT_DIR,runtime:renderedReview.classifyRuntime(pins),
  renderer:{loader:loaderResource.sha256,wasm:wasmResource.sha256}}) : null;
if (WORK_MAJOR_HEADING_CHECKS) {
  assert.equal(renderedReviewPreflight.profile,16,'heading order fixture requires exact P16');
  assert.equal(renderedReviewPreflight.renderer.schemaVersion,2,'heading order fixture requires exact layout2 renderer');
}

// --- Plain fixture: exact independently-accepted plain baseline extracted from
// the controlled repo file tests/ModernCompilerBrowserHarness/ModernApi.cs
// (`var source = "..."` regular C# literal; JSON-compatible escapes). Extracted
// with a bounded regex + JSON.parse, UTF-8 encoded — NOT hand-reconstructed.
// This is an ASCII plain check (article, amsmath, kernel typeout, section
// label/ref); it intentionally does NOT request T2A, which the current bundle
// lacks a default T2Acmr.fd for. Assert the lead-confirmed hash before UI import.
const PLAIN_HASH = "e8a7dd410a60afab22dca120cd749ed12fb460dd0d337913c5c988b112082b54"; // 251 bytes
const PLAIN_MAIN = (() => {
  const apiSrc = fs.readFileSync(
    path.join(__dirname, "../tests/ModernCompilerBrowserHarness/ModernApi.cs"),
    "utf8",
  );
  const m = apiSrc.match(/var source = "((?:[^"\\]|\\.)*)";/);
  if (!m) throw new Error("ModernApi plain source literal not found");
  let bytes;
  try { bytes = Buffer.from(JSON.parse('"' + m[1] + '"'), "utf8"); }
  catch { throw new Error("ModernApi plain source literal must be JSON-compatible escapes"); }
  assert.equal(sha256(bytes), PLAIN_HASH, "plain fixture hash mismatch");
  return bytes;
})();
const MAJOR_HEADING_MAIN = workMajorHeading?.fixture(PLAIN_MAIN);

// --- Golden fixture: exact accepted bytes extracted from the controlled
// repository file tests/ModernCompilerBrowserHarness/AuthoringFixture.cs
// (constants Main, Chapter, Bibliography), not a hand-written "mirrors"
// variant and not the runtime sources.json (a filename->path map, not document
// bytes). The raw triple-quoted strings have their 8-space closing-delimiter
// indentation stripped per line; opening/closing delimiter newlines excluded.
// The three hashes below were independently confirmed by the lead against the
// real accepted browser04 snapshot; assert them before UI import.
const GOLDEN_HASHES = {
  "main.tex": "9311f6e211026151d0a241395b239b6940a30ce948db8cbbedf94609439a1798", // 1505 bytes
  "chapter.tex": "803d336ebebbe9f5cde06a05d26e7cc60c5c325fcdd0ea704a078b3af2730a91", // 246 bytes
  "references.bib": "8020371a10a37348647a8f1f57dad2256e109c7929886dfa90e3038e7ed067f2", // 365 bytes
};
let goldenMain, goldenChapter, goldenRefs;
if (CASE === "golden" || PREFLIGHT_ONLY) {
  const fixtureSrc = fs.readFileSync(
    path.join(__dirname, "../tests/ModernCompilerBrowserHarness/AuthoringFixture.cs"),
    "utf8",
  );
  function extractRaw(name) {
    const re = new RegExp('string ' + name + '\\s*=\\s*"""\\s*\\n([\\s\\S]*?)\\n\\s*"""' + ';');
    const m = fixtureSrc.match(re);
    if (!m) throw new Error('AuthoringFixture constant not found: ' + name);
    // Strip exactly the 8-space closing-delimiter indentation from each line.
    return Buffer.from(
      m[1].split('\n').map((l) => (l.startsWith('        ') ? l.slice(8) : l)).join('\n'),
      'utf8',
    );
  }
  goldenMain = extractRaw('Main');
  goldenChapter = extractRaw('Chapter');
  goldenRefs = extractRaw('Bibliography');
  for (const [name, bytes] of [['main.tex', goldenMain], ['chapter.tex', goldenChapter], ['references.bib', goldenRefs]]) {
    assert.ok(bytes.length > 0, 'golden fixture empty: ' + name);
    assert.equal(sha256(bytes), GOLDEN_HASHES[name], 'golden fixture hash mismatch: ' + name);
  }
}

// --- Long fixture: 64 imported files (main + 62 \include chapters + appendix),
// \tableofcontents AFTER \begin{document}, \include for page boundaries.
// --- Long fixture: ASCII 64-file TOC/state capacity test (NOT a T2A font test).
// main + 62 \include chapters + 1 appendix = 64 imported files. article 11pt,
// inputenc, \begin{document} THEN \tableofcontents, \include for page
// boundaries. No T2A/OT1 fontenc load (the current bundle lacks a default
// T2Acmr.fd); this is an ASCII capacity/convergence test, not a font test.
function longFixture() {
  const parts = [
    "\\documentclass[11pt]{article}\n",
    "\\usepackage[utf8]{inputenc}\n",
    "\\begin{document}\n",
    "\\tableofcontents\n",
    "\\newpage\n",
    "\\section{Long authoring root}\\label{sec:root}\n",
    "Long TOC fixed-point proof: Kyiv 000.\n",
    "\\newpage\n",
  ];
  for (let i = 0; i < 62; i++) parts.push(`\\include{chapter${String(i).padStart(2, "0")}}\n`);
  parts.push("\\include{appendix}\n");
  parts.push("\\end{document}\n");
  const main = Buffer.from(parts.join(""), "utf8");
  const chapters = [];
  for (let i = 0; i < 62; i++) {
    chapters.push({
      name: `chapter${String(i).padStart(2, "0")}.tex`,
      buffer: Buffer.from(
        `\\section{Chapter ${i}}\\label{sec:c${i}}\nChapter ${i} body: Kyiv ${i}.\n`,
        "utf8",
      ),
    });
  }
  chapters.push({
    name: "appendix.tex",
    buffer: Buffer.from("\\section{Appendix}\\label{sec:app}\nAppendix body: Kyiv end.\n", "utf8"),
  });
  return { main, chapters };
}

// Private project inputs only: not another compiler profile, font installer or vendor bundle.
const FONT_IDENTITIES = {
  "main.tex": [237, "3ca9b7bf73171da48363e5d4c37993d37396764742bab04f4eefabd51344c1bd"],
  "t2acmr.fd": [2556, "193f28c15cf5506b89694f1a155307016cc00a3eec05f07c2c6cb4e6835580b5"],
  "larm1000.tfm": [3220, "edf9222464ab7249229895bc59e612d9d7e5538777a856f8fda689249b364d64"],
  "labx1000.tfm": [3300, "07d9c79950301380f351a6fc6fd25f9c8aabcb9ca964d99d496024b835753f1b"],
  "cm-super-t2a.enc": [3141, "bbf41f1c0858e9ba489dfd9a6290d17b189345c1e982a096efe4de4547fdff31"],
  "sfrm1000.pfb": [138258, "650a5428cc0729742dcf1935dba1f504e27bbb5ec5936aeaf5f4955909a0f540"],
  "sfbx1000.pfb": [145408, "b6ef43f479c1e94d1414ecd09246af4ee082444deed96a82cd4b6b8cd8fb4566"],
};
const FONT_FILES = CASE === "font-assets" ? (() => {
  const dir = req("T2A_FONT_PROJECT_DIR");
  assert.deepEqual(fs.readdirSync(dir).sort(), Object.keys(FONT_IDENTITIES).sort(), "exact7 font-project names");
  const files = Object.entries(FONT_IDENTITIES).map(([name, [size, digest]]) => {
    const file = path.join(dir, name);
    assert.ok(fs.lstatSync(file).isFile(), `font project input must be a regular file: ${name}`);
    assert.ok(size <= 1048576, "actual app per-file capture cap");
    assert.equal(fs.statSync(file).size, size, `font input bounded size before read: ${name}`);
    const buffer = fs.readFileSync(file);
    assert.equal(buffer.length, size, `font bytes: ${name}`);
    assert.equal(sha256(buffer), digest, `font input hash: ${name}`);
    return { name, buffer, mimeType: /\.(?:tfm|pfb)$/.test(name) ? "application/octet-stream" : "text/plain" };
  });
  assert.equal(files.reduce((n, file) => n + file.buffer.length, 0), 296120);
  assert.ok(files.length <= 64 && files.reduce((n, file) => n + file.buffer.length, 0) <= 8388608);
  const example = fs.readFileSync(path.join(__dirname, "../docs/examples/t2a-project-font-main.tex"));
  assert.deepEqual(example, files.find(file => file.name === "main.tex").buffer, "source-only repo example matches immutable main exactly");
  return files;
})() : null;

// Only AFTER the unchanged original7-file/296120B checks above: an author explicitly
// opts into a DIFFERENT pinned source example, never a mutation of private inputs.
const REPAIR_MAIN_IDENTITY = { bytes: 739, sha256: "55fbc51d1952f092feabf38140cbc0cc6335cd13541ea2299a0d8bcb59e444c3" };
const EFFECTIVE_FONT_FILES = T2A_UNICODE_REPAIR ? (() => {
  const examplePath = path.join(__dirname, "../docs/examples/t2a-cyrillic-unicode-main.tex");
  assert.ok(fs.lstatSync(examplePath).isFile(), "explicit repair example is a regular source file");
  assert.equal(fs.statSync(examplePath).size, REPAIR_MAIN_IDENTITY.bytes);
  const buffer = fs.readFileSync(examplePath);
  assert.equal(buffer.length, REPAIR_MAIN_IDENTITY.bytes);
  assert.equal(sha256(buffer), REPAIR_MAIN_IDENTITY.sha256, "explicit repaired source pin");
  const files = FONT_FILES.map(file => file.name === "main.tex" ? { ...file, buffer } : file);
  assert.equal(files.length, 7); assert.ok(files.length <= 64);
  assert.ok(files.every(file => file.buffer.length <= 1048576), "effective app per-file capture bound");
  assert.ok(files.reduce((sum, file) => sum + file.buffer.length, 0) <= 8388608, "effective app total capture bound");
  return files;
})() : FONT_FILES;
const fontIdentities = files => files?.map(file => ({ name: file.name, bytes: file.buffer.length, sha256: sha256(file.buffer) })) || null;
const fontSourceSelection = () => ({
  explicitRepair: T2A_UNICODE_REPAIR,
  originalInputs: fontIdentities(FONT_FILES), originalTotalBytes: FONT_FILES?.reduce((sum, file) => sum + file.buffer.length, 0) || 0,
  effectiveInputs: fontIdentities(EFFECTIVE_FONT_FILES), effectiveTotalBytes: EFFECTIVE_FONT_FILES?.reduce((sum, file) => sum + file.buffer.length, 0) || 0,
  originalMain: FONT_FILES ? fontIdentities(FONT_FILES).find(file => file.name === "main.tex") : null,
  effectiveMain: EFFECTIVE_FONT_FILES ? fontIdentities(EFFECTIVE_FONT_FILES).find(file => file.name === "main.tex") : null,
});

if (PREFLIGHT_ONLY) {
  assert.equal(PLAIN_MAIN.length, 251);
  assert.equal(goldenMain.length, 1505); assert.equal(goldenChapter.length, 246); assert.equal(goldenRefs.length, 365);
  const long = longFixture(), entries = [{ name: "main.tex", buffer: long.main }, ...long.chapters];
  assert.equal(entries.length, 64); assert.equal(new Set(entries.map(file => file.name)).size, 64);
  process.stdout.write(JSON.stringify({ result: "PREFLIGHT_ONLY", browserNativeProof: false, sdkVerified: false, pdfDownloadChecksEnabled: CHECK_DOWNLOADS, bundledRuntime: BUNDLED_RUNTIME,
    plain: { bytes: PLAIN_MAIN.length, sha256: sha256(PLAIN_MAIN) },
    ...(MAJOR_HEADING_MAIN ? {majorHeadingFixture:{bytes:MAJOR_HEADING_MAIN.length,sha256:sha256(MAJOR_HEADING_MAIN)}} : {}),
    golden: [["main.tex", goldenMain], ["chapter.tex", goldenChapter], ["references.bib", goldenRefs]]
      .map(([name, bytes]) => ({ name, bytes: bytes.length, sha256: sha256(bytes) })),
    long: entries.map(file => ({ name: file.name, bytes: file.buffer.length, sha256: sha256(file.buffer) })),
    fontAssets: FONT_FILES?.map(file => ({ name: file.name, bytes: file.buffer.length, sha256: sha256(file.buffer) })) || null,
    fontSourceSelection: fontSourceSelection(), finalR1: r1Preflight(),
    sourcePdfChecksEnabled:SOURCE_PDF_CHECKS,packageCompilerOnly:packageCompilerScope(),
    renderedReview:renderedReviewPreflight,
    packageInputs:PACKAGE_CASE?portableFiles.map(f=>({name:f.name,length:f.buffer.length,sha256:sha256(f.buffer)})):null,
  }) + "\n");
  process.exit(0); // BEFORE http.createServer, profile creation, Chromium or runtime execution.
}

// --- Owned static origin (inline, proven headers + path guard; dedicated port).
const TYPES = {
  ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8",
  ".mjs": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8", ".wasm": "application/wasm",
  ".png": "image/png", ".svg": "image/svg+xml", ".ico": "image/x-icon",
  ".txt": "text/plain; charset=utf-8", ".dat": "application/octet-stream",
  ".blat": "application/octet-stream", ".pdb": "application/octet-stream",
};
const server = http.createServer((request, response) => {
  let url;
  try { url = new URL(request.url, "http://127.0.0.1:" + PORT); }
  catch { response.writeHead(400); response.end("bad url"); return; }
  const name = url.pathname === "/" ? "/index.html" : url.pathname;
  if (name.includes("..")) { response.writeHead(400); response.end("bad path"); return; }
  const file = path.resolve(WWWROOT, "." + name);
  if (!file.startsWith(path.resolve(WWWROOT) + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
    response.writeHead(404); response.end("not found"); return;
  }
  response.writeHead(200, {
    "Content-Type": TYPES[path.extname(file).toLowerCase()] || "application/octet-stream",
    "Cache-Control": "no-store",
  });
  fs.createReadStream(file).pipe(response);
});

// Whole-browser Chromium census: pid + startticks + executable basename.
function chromiumCensus() {
  const out = [];
  try {
    for (const entry of fs.readdirSync("/proc")) {
      if (!/^\d+$/.test(entry)) continue;
      const pid = parseInt(entry, 10);
      let exe = "";
      try { exe = fs.readlinkSync(`/proc/${pid}/exe`); } catch { continue; }
      const base = path.basename(exe);
      if (/chromium|chrome|headless_shell/i.test(base)) {
        let startticks = "";
        try { startticks = fs.readFileSync(`/proc/${pid}/stat`, "utf8").split(") ")[1].split(" ")[19]; } catch {}
        out.push({ pid, startticks, exe: base });
      }
    }
  } catch {}
  return out;
}

const record = {
  result: "FAIL", case: CASE, compilerChoice: COMPILER_CHOICE,
  scope: PACKAGE_COMPILER_ONLY ? "local-authoring-compiler-package-cold-offline; native navigation NOT ASSESSED pending R2" : "local-authoring-compile-pdf-cold-offline",
  pdfDownloadChecksEnabled: CHECK_DOWNLOADS, bundledRuntime: BUNDLED_RUNTIME,
  sourcePdfChecksEnabled: SOURCE_PDF_CHECKS,
  renderedReview:renderedReviewPreflight,
  harnessSha256: sha256(fs.readFileSync(__filename)),
  sourcePdfHelperSha256: sha256(fs.readFileSync(path.join(__dirname, 'source-pdf-navigation-ui-checks.cjs'))),
  renderedReviewHelperSha256:sha256(fs.readFileSync(path.join(__dirname,'rendered-review-ui-checks.cjs'))),
  ...(MAJOR_HEADING_MAIN ? {workMajorHeadingHelperSha256:sha256(fs.readFileSync(path.join(__dirname,'work-major-heading-ui-checks.cjs')))} : {}),
  sdk: { cspValid: true, manifestVersion: manifest.version, manifestAssets: manifest.assets.length, allAssetHashesVerified: true },
  resources: {
    compilerPins: pins.length, pinsSha256: sha256(fs.readFileSync(pinsPath)),
    pdfResources: pdfManifest.resources.length, pdfManifestSha256: sha256(fs.readFileSync(pdfManifestPath)),
    loaderSha256: loaderResource.sha256, wasmSha256: wasmResource.sha256, verifiedAgainstManifest: true,
  },
  fontSourceSelection: fontSourceSelection(), finalR1: r1Preflight(),packageCompilerOnly:packageCompilerScope(),
  online: {}, coldOffline: {}, skipped: null, failure: null,
};
if (PACKAGE_CASE) record.packagePdfFeatureCoverage = {
  links: { status: 'not-assessed', expectedUri: 'https://example.org/',
    reason: 'Registered PDF preview does not request or expose passive URI actions; a rendered text label is not a link check.' },
  bookmarks: { status: 'not-assessed', expectedTitles: ['Compiler suite', 'Included chapter'],
    reason: 'Registered PDF preview and E1 inspection contract do not expose outlines; inspect the exact retained PDF artifacts separately.' },
};
if(PACKAGE_COMPILER_ONLY){
  record.packagePdfFeatureCoverage.nativeNavigation={...record.packageCompilerOnly.nativeNavigation};
  record.online.nativeNavigation={...record.packageCompilerOnly.nativeNavigation};
  record.coldOffline.nativeNavigation={...record.packageCompilerOnly.nativeNavigation};
}
let phase = "setup";
function save() {
  if (RESULT_DIR) {
    fs.writeFileSync(path.join(RESULT_DIR, "local-authoring-receipt.json"), JSON.stringify(record, null, 2) + "\n");
  }
}
// Bounded hard deadline (browser run is lead-owned at 360s; harness logic stays finite).
const deadline = setTimeout(() => {
  record.failure = { phase, message: "harness deadline 360s" }; save(); process.exit(124);
}, 360000);

// Deterministic finite wait helpers; no poll-until-success.
async function busyFalse(page, testid, timeoutMs) {
  await page.waitForFunction(
    (t) => document.querySelector(`[data-testid="${t}"]`)?.dataset.busy === "false",
    testid,
    { timeout: timeoutMs || 120000 },
  );
}
async function runtimeState(page, state, timeoutMs, previousOperation = null) {
  if (previousOperation !== null) await page.waitForFunction(previous => {
    const status = document.querySelector('[data-testid=runtime-status]');
    return Number(status?.dataset.operation) > previous && status?.dataset.busy === 'false';
  }, previousOperation, {timeout: timeoutMs || 120000});
  await busyFalse(page, "runtime-status", timeoutMs);
  assert.equal(
    await H.$(page, "runtime-status").getAttribute("data-state"),
    state,
    `runtime-status: ${await H.$(page, "runtime-status").textContent()}`,
  );
}
async function pdfRuntimeState(page, state, timeoutMs) {
  await busyFalse(page, "pdf-runtime-status", timeoutMs);
  assert.equal(
    await H.$(page, "pdf-runtime-status").getAttribute("data-state"),
    state,
    `pdf-runtime-status: ${await H.$(page, "pdf-runtime-status").textContent()}`,
  );
}
async function waitReadiness(page, ready, timeoutMs) {
  await page.waitForFunction(
    (r) => {
      const t = document.querySelector("[data-testid=compiler-readiness]")?.textContent || "";
      return r ? t.startsWith("Local compiler checked") : /Compiler unavailable/.test(t);
    },
    ready,
    { timeout: timeoutMs || 60000 },
  );
}
async function waitCompileDone(page, timeoutMs) {
  const start = Date.now();
  await page.waitForFunction(
    () => {
      const s = document.querySelector("[data-testid=compile-status]")?.textContent || "";
      return /^Compilation finished\./.test(s) || /^No new PDF:/.test(s);
    },
    null,
    { timeout: timeoutMs || 90000 },
  );
  return Date.now() - start;
}
async function waitPreviewAvailable(page, timeoutMs) {
  await page.waitForFunction(
    () => {
      const p = document.querySelector("[data-testid=preview-status]");
      const c = document.querySelector("[data-testid=canvas-status]");
      const pane = document.querySelector("[data-testid=pdf-pane]");
      if (["Error", "TimedOut", "Unavailable"].includes(p?.dataset.previewStatus)) return true;
      return (
        p?.dataset.previewStatus === "Available" &&
        /Page ready/.test(p?.textContent || "") &&
        /Drawn/.test(c?.textContent || "") &&
        pane?.dataset.document
      );
    },
    null,
    { timeout: timeoutMs || 90000 },
  );
  assert.equal(await previewStatus(page), "Available", await H.$(page, "pdf-details").textContent());
}
async function previewStatus(page) {
  return H.$(page, "preview-status").getAttribute("data-preview-status");
}
async function pageCount(page) {
  const label = (await H.$(page, "page-label").textContent()).trim();
  const m = label.match(/of (\d+)/);
  return m ? parseInt(m[1], 10) : 0;
}
async function canvasNonwhite(page) {
  return page.evaluate(() => {
    const c = document.querySelector('[data-testid=pdf-canvas]');
    if (!c || !c.width || !c.height) return 0;
    const { data } = c.getContext("2d").getImageData(0, 0, c.width, c.height);
    let n = 0;
    for (let i = 0; i < data.length; i += 4)
      if (data[i] < 250 || data[i + 1] < 250 || data[i + 2] < 250) n++;
    return n;
  });
}
async function pageText(page) {
  return (await H.$(page, "page-text").textContent()) || "";
}
// Navigate once through the real input; an already-current page needs no event.
async function goToPage(page, n) {
  const wait = () => page.waitForFunction((target) => {
    const match = (document.querySelector('[data-testid=page-label]')?.textContent || '').match(/Page (\d+) of (\d+)/);
    return !!match && Number(match[1]) === target
      && document.querySelector('[data-testid=preview-status]')?.dataset.previewStatus === 'Available'
      && /Drawn/.test(document.querySelector('[data-testid=canvas-status]')?.textContent || '');
  }, n, {timeout: 30000});
  const label = await H.$(page, 'page-label').textContent();
  const current = Number(label.match(/Page (\d+) of/)?.[1] || 0);
  if (current !== n) {
    const input = page.locator('input[aria-label="Go to page"]');
    await input.fill(String(n));
    await input.press('Tab');
  }
  await wait();
}
// Collect text across all pages (bounded to actual page count; for golden/long).
async function collectAllPageText(page) {
  const n = await pageCount(page);
  const texts = [];
  for (let i = 1; i <= n; i++) {
    await goToPage(page, i);
    texts.push(await pageText(page));
  }
  return texts.join("\n");
}

async function verifyLongSearch(page, project, marker, hash, label) {
  const n = await pageCount(page);
  assert.ok(n > 64 && n <= 512);
  const head = await H.head(page, project);
  const editor = await H.$(page, "editor").inputValue();
  const details = await H.$(page, "compile-details").textContent();
  const current = await H.$(page, "compile-panel").getAttribute("data-is-current");
  const beforeFile = `${label}-before.pdf`, afterFile = `${label}-after.pdf`;
  const before = await downloadAcceptedPdf(page, project, `${label}-before`, beforeFile, hash);
  phase = label;
  await H.$(page, "pdf-pane").locator('input[aria-label="Search PDF"]').fill(marker);
  async function select(action) {
    const generation = await H.$(page, "pdf-pane").getAttribute("data-generation");
    await action();
    await page.waitForFunction(old =>
      document.querySelector('[data-testid="pdf-pane"]').dataset.generation !== old,
      generation, { timeout: 10000 });
    await waitPreviewAvailable(page, 90000);
  }
  await select(() => page.getByRole("button", { name: "Search document", exact: true }).click());
  const status = H.$(page, "search-status");
  const more = H.$(page, "continue-pdf-search");
  const batches = [];
  // Finite, explicit UI clicks in the test; the application never auto-continues.
  for (let batch = 1; batch <= Math.ceil(n / 32); batch++) {
    await waitPreviewAvailable(page, 90000);
    const scanned = Number(await status.getAttribute("data-scanned-pages"));
    const matches = Number(await status.getAttribute("data-match-count"));
    const state = await status.getAttribute("data-search-status");
    assert.equal(Number(await status.getAttribute("data-total-pages")), n);
    assert.equal(scanned, Math.min(batch * 32, n));
    assert.equal(matches, scanned === n ? 1 : 0);
    assert.equal(state, scanned === n ? "Complete" : "Partial");
    batches.push({ scanned, matches, state });
    if (scanned === n) {
      assert.equal(await more.isDisabled(), true);
      break;
    }
    // Click waits for admission to settle and the button to become enabled.
    await select(() => more.click());
  }
  assert.equal(batches.at(-1).scanned, n);
  await select(() => page.getByRole("button", { name: "Next result", exact: true }).click());
  assert.equal(await H.$(page, "page-label").textContent(), `Page ${n} of ${n}`);
  assert.match(await H.$(page, "selected-result").textContent(), /Result 1 \/ 1/);
  assert.ok(await H.$(page, "glyph-highlight").count() > 0);
  assert.ok((await canvasNonwhite(page)) > 0);
  assert.ok((await pageText(page)).includes(marker));
  await select(() => page.getByRole("combobox", { name: "Zoom", exact: true }).selectOption("125"));
  assert.equal(await status.getAttribute("data-search-status"), "Complete");
  assert.equal(Number(await status.getAttribute("data-scanned-pages")), n);
  assert.equal(Number(await status.getAttribute("data-match-count")), 1);
  assert.ok(await H.$(page, "glyph-highlight").count() > 0);
  await page.screenshot({ path: path.join(RESULT_DIR, `${label}.png`), fullPage: true });
  const after = await downloadAcceptedPdf(page, project, `${label}-after`, afterFile, hash);
  assert.deepEqual(fs.readFileSync(path.join(RESULT_DIR, beforeFile)),
    fs.readFileSync(path.join(RESULT_DIR, afterFile)), "exact PDF bytes unchanged by search");
  assert.deepEqual(await H.head(page, project), head);
  assert.equal(await H.$(page, "editor").inputValue(), editor);
  assert.equal(await H.$(page, "compile-details").textContent(), details);
  assert.equal(await H.$(page, "compile-panel").getAttribute("data-output-hash"), hash);
  assert.equal(await H.$(page, "pdf-pane").getAttribute("data-document"), hash);
  assert.equal(await H.$(page, "compile-panel").getAttribute("data-is-current"), current);
  return { marker, hash, pageCount: n, batches, before, after, exactBytesUnchanged: true };
}

// Actual renderer and exported Rust evaluation through registered UI. The
// explicit labels below are synthetic author input, not structural detection.
async function verifySectionRanges(page, project, hash, label, reconfirm) {
  phase = label;
  const before = await H.head(page, project), source = await H.$(page, "editor").inputValue();
  const details = await H.$(page, "compile-details").textContent();
  await H.reveal(page, H.$(page, "rendered-extent-check"));
  const enable = H.$(page, "rendered-section-enable");
  if (reconfirm) {
    assert.equal(await enable.isChecked(), true, "explicit ranges retained across compile");
    await H.$(page, "section-ranges-stale").waitFor();
    assert.equal(await H.$(page, "rendered-extent-check").isDisabled(), true);
    await H.$(page, "section-ranges-confirm").click();
  } else {
    await enable.check();
    await H.$(page, "rendered-extent-work-type").selectOption("bachelorQualification");
    for (const [name, value] of Object.entries({
      "section-intro-start":1, "section-intro-end":4, "section-body-start":5,
      "section-body-end":29, "section-conclusions-start":30, "section-conclusions-end":35,
      "section-appendix-start":50,
    })) { await H.$(page, name).fill(String(value)); await H.$(page, name).press("Tab"); }
    await H.$(page, "section-ranges-confirm").click();
  }
  await H.$(page, "rendered-extent-check").click();
  await page.waitForFunction(() =>
    document.querySelector('[data-testid="rendered-extent-result"]')?.dataset.current === "true",
    null, { timeout: 60000 });
  const result = H.$(page, "rendered-extent-result");
  assert.equal(await result.getAttribute("data-pdf-sha256"), hash);
  assert.equal(await result.getAttribute("data-profile-version"), "2.0.0");
  assert.equal(await result.getAttribute("data-status"), "withinUsualRange");
  assert.equal(await result.getAttribute("data-page-count"), String(await pageCount(page)));
  assert.equal(await result.getAttribute("data-work-type"), "bachelorQualification");
  assert.equal(await result.getAttribute("data-reason"), "classifiedNonAppendixWithinUsualRange");
  const expected = [
    ["KNU-CSC-2025-PDF-INTRODUCTION-001", "aboveMaximum",4,"none",3],
    ["KNU-CSC-2025-PDF-BODY-001", "withinLimit",25,25,"none"],
    ["KNU-CSC-2025-PDF-CONCLUSIONS-001", "aboveMaximum",6,"none",5],
  ];
  for (const [id,status,pages,min,max] of expected) {
    const item = result.locator(`[data-testid="rendered-section-result"][data-rule-id="${id}"]`);
    for (const [name,value] of Object.entries({status,pages,minimum:min,maximum:max}))
      assert.equal(await item.getAttribute("data-"+name), String(value));
  }
  assert.match(await H.$(page, "rendered-extent-coverage").textContent(), /labels supplied by you/);
  assert.equal(await H.$(page, "rendered-extent-citation").getAttribute("href"), "https://csc.knu.ua/uk/filer/canonical/1740300611/2680/");
  assert.deepEqual(await H.head(page, project), before);
  assert.equal(await H.$(page, "editor").inputValue(), source);
  assert.equal(await H.$(page, "compile-details").textContent(), details);
  assert.equal(await H.$(page, "compile-panel").getAttribute("data-output-hash"), hash);
  assert.equal(await H.$(page, "compile-panel").getAttribute("data-is-current"), "true");
  await page.screenshot({ path: path.join(RESULT_DIR, `${label}.png`), fullPage: true });
  return {hash, pageCount:await pageCount(page), profileVersion:"2.0.0", userLabels:true,
    nonAppendixPages:49, observations:expected, unchangedSourceAndHead:true};
}

// Select main.tex through the real file-tree UI (import may leave another file
// active), then assert the editor shows main.tex.
async function selectMainInTree(page) {
  await page.locator('[data-testid="select-file"]', { hasText: MAIN_PATH }).first().click();
  await page.locator(".editor-toolbar label", { hasText: MAIN_PATH }).waitFor();
}

function assertPackageHead(head, expectedMainBytes) {
  assert.equal(head.files.length, portableFiles.length, 'exact unchanged package fixture file set');
  return portableFiles.map(input => {
    const filePath = input.name === 'main.tex' ? MAIN_PATH : input.name;
    const file = head.files.find(value => value.path === filePath);
    assert.ok(file, `saved package input: ${filePath}`);
    const expected = input.name === 'main.tex' ? expectedMainBytes : input.buffer;
    const bytes = Buffer.from(file.bytes);
    assert.deepEqual(bytes, expected, `saved exact package input bytes: ${filePath}`);
    return { path: filePath, bytes: bytes.length, sha256: sha256(bytes) };
  });
}

function assertFontHead(head, expectedMainBytes) {
  assert.deepEqual(head.files.map(file => file.path).sort(), Object.keys(FONT_IDENTITIES).sort(), "persisted exact7 project files");
  return FONT_FILES.map(input => {
    const file = head.files.find(file => file.path === input.name);
    const bytes = Buffer.from(file.bytes), expected = input.name === "main.tex" ? expectedMainBytes : input.buffer;
    assert.deepEqual(bytes, expected, `persisted raw bytes unchanged: ${input.name}`);
    return { name: input.name, bytes: bytes.length, sha256: sha256(bytes) };
  });
}
function assertFontLog(log) {
  assert.ok(log && log.length > 0, "font compiler log present");
  assert.doesNotMatch(log, /Missing character:|LaTeX Font Warning:|Font shape[^\n]*undefined|Some font shapes were not available|Metric\s*\(TFM\)[^\n]*(?:not found|failed)|font[^\n]*(?:not loadable|substitut)|(?:cannot open|can't find|failed to load)[^\n]*(?:font|\.tfm|\.pfb|\.enc|\.fd)|(?:\.tfm|\.pfb|\.enc|\.fd)[^\n]*(?:not found|cannot open|failed)/i,
    "no missing glyph/font, substitution or TFM/outline/encoding load failure");
  assert.ok(log.includes("sfrm1000.pfb") && log.includes("sfbx1000.pfb"), "actual native log mentions both mapped Type1 font programs");
  return { boundedLog: log.slice(0, 16384), logTruncated: log.length > 16384,
    fontProgramsMentioned: ["sfrm1000.pfb", "sfbx1000.pfb"], independentEmbeddingProof: false };
}
function assertFontPageText(text) {
  const counts = {};
  for (const marker of ["Привіт", "Київ", "Іі", "Її", "Єє", "Ґґ"]) {
    counts[marker] = text.split(marker).length - 1;
    assert.equal(counts[marker], 2, `literal regular+bold page-text occurrences: ${marker}`);
  }
  return { literalOccurrences: counts, normalizationApplied: false, sourceContext: "exact10pt regular + textbf input", independentEmbeddingProof: false };
}
function assertRepairLatinText(text) {
  const marker = "ASCII I i 123.";
  const count = text.split(marker).length - 1;
  assert.equal(count, 2, "exact two OT1 Latin regular+bold controls, without Unicode folding");
  for (const wrong of ["ASCII І i 123.", "ASCII I і 123.", "ASCII І і 123."]) {
    assert.ok(!text.includes(wrong), "Latin control must not contain Cyrillic І/і replacements");
  }
  return { literal: marker, occurrences: count, cyrillicReplacementsAllowed: false, normalizationApplied: false };
}
function assertCapturedR1Head(head, expectedMain, biberEdited=false) {
  assert.equal(head.files.length,capturedR1Files.length,'exact captured R1 input set');
  return capturedR1Files.map(input=>{
    const name=input.name==='main.tex'?MAIN_PATH:input.name,file=head.files.find(f=>f.path===name);
    assert.ok(file,'captured R1 input '+name);const bytes=Buffer.from(file.bytes);
    const expected=input.name==='main.tex'?expectedMain:biberEdited&&input.name===BIBER_COLD_EDIT.file?changedBiberBib:input.buffer;
    assert.deepEqual(bytes,expected,'exact captured R1 input '+name);
    return {name,length:bytes.length,sha256:sha256(bytes)};
  });
}
async function editBiberBibliography(page,project,before,expectedMain) {
  assert.ok(BIBER_CASE);assertCapturedR1Head(before,expectedMain);
  await page.locator('[data-testid="select-file"]',{hasText:BIBER_COLD_EDIT.file}).first().click();
  await page.locator('.editor-toolbar label',{hasText:BIBER_COLD_EDIT.file}).waitFor();
  const original=portableFiles.find(f=>f.name===BIBER_COLD_EDIT.file).buffer;
  assert.equal(await H.$(page,'editor').inputValue(),original.toString('utf8'),'original BIB reopened through real editor');
  await H.$(page,'editor').fill(changedBiberBib.toString('utf8'));await H.save(page);
  const saved=await H.head(page,project),expected=before.files.map(f=>f.path===BIBER_COLD_EDIT.file?{...f,bytes:Array.from(changedBiberBib)}:f);
  assert.deepEqual(saved.files,expected,'BIB Save appends only the new entry and preserves every other input');
  assert.notEqual(saved.id,before.id,'changed BIB has a new durable project revision');
  await selectMainInTree(page);assert.equal(await H.$(page,'editor').inputValue(),expectedMain.toString('utf8'));
  return {file:BIBER_COLD_EDIT.file,key:BIBER_COLD_EDIT.key,marker:BIBER_COLD_EDIT.marker,
    beforeHeadId:before.id,afterBibSaveHeadId:saved.id,
    original:{length:original.length,sha256:sha256(original)},changed:{length:changedBiberBib.length,sha256:sha256(changedBiberBib)},
    action:'real file tree/editor append and explicit Save; no storage writes or generated inputs injected'};
}
function captureBiberEdit(head,before,source) {
  assertCapturedR1Head(head,source,true);
  const expected=before.files.map(f=>f.path===MAIN_PATH?{...f,bytes:Array.from(source)}:f.path===BIBER_COLD_EDIT.file?{...f,bytes:Array.from(changedBiberBib)}:f);
  assert.deepEqual(head.files,expected,'only main citation and selected BIB change; all other IDs/paths/bytes preserved');
  const rows=[];
  for(const [name,filename]of [[MAIN_PATH,'cold-offline-main.tex'],[BIBER_COLD_EDIT.file,'cold-offline-references-latin.bib']]){
    const bytes=Buffer.from(head.files.find(f=>f.path===name).bytes);fs.writeFileSync(path.join(RESULT_DIR,filename),bytes);
    rows.push({name,filename,length:bytes.length,sha256:sha256(bytes),headId:head.id});
  }
  return {changedInputs:rows,changedPaths:rows.map(r=>r.name),otherInputsPreserved:true,generatedInputsSupplied:false};
}
function assertFinalR1Log(log) {
  const banners=[...log.matchAll(/(?:^|\n)This is (?:pdfTeX|XeTeX|LuaHBTeX|LuaTeX), Version[^\r\n]*/g)];
  assert.ok(banners.length>=(BIBER_CASE?3:2),'actual multiple current TeX passes');
  const final=log.slice(banners.at(-1).index);
  assert.doesNotMatch(final,/undefined|(?:re)?run (?:LaTeX|Biber)|Please.*rerun|Label\(s\) may have changed|Missing character:|LaTeX Font Warning:|not loadable|File[^\n]*not found/i,'clean final genuine TeX pass');
  assert.doesNotMatch(log,/Missing character:|LaTeX Font Warning:|not loadable|File[^\n]*not found/,'no missing font/package in any pass');
  if(BIBER_CASE){assert.match(log,/This is Biber 2\.19/);assert.match(log,/(?:Writing|Output to)[^\n]*\.bbl/i,'genuine Biber wrote the BBL');}
  return {texPassBanners:banners.map(b=>b[0].trim()),finalPassLog:final.slice(0,16384),finalPassLogSha256:sha256(Buffer.from(final)),finalPassClean:true,
    bblEvidence:BIBER_CASE?'genuine Biber writing log plus actual rendered bibliography; direct BBL bytes belong to native producer proof':null};
}
function assertPackageCompilerLog(log) {
  const result=assertFinalR1Log(log);
  assert.ok(result.texPassBanners.length>=3,'explicit BibTeX package regression requires at least three genuine TeX passes');
  assert.doesNotMatch(log,/This is Biber/,'compiler-only BibTeX fixture cannot substitute Biber');
  return {...result,bibtexBannerPresent:/This is BibTeX, Version 0\.99d/.test(log),
    backendProof:'read-only production bridge phase trace; the legacy BibTeX banner is not guaranteed in the merged log'};
}
function assertBiberEditRevision(online,offline,details,log) {
  const old=online.trace.find(r=>r.tag.tool==='biber').tag,current=offline.trace.find(r=>r.tag.tool==='biber').tag;
  assert.match(old.runtimeIdentity,/^[0-9a-f]{64}$/);assert.equal(current.runtimeIdentity,old.runtimeIdentity,'same installed runtime across whole-browser restart and BIB edit');
  for(const r of [...online.trace,...offline.trace])assert.equal(r.tag.runtimeIdentity,old.runtimeIdentity);
  assert.match(current.digest,/^[0-9a-f]{64}$/);assert.notEqual(current.digest,old.digest,'production project-input fingerprint changes with saved source/BIB');
  const uiDigest=details.match(/Input: ([0-9a-f]{64})/)?.[1];assert.equal(uiDigest,current.digest,'accepted UI revision digest matches actual backend phase');
  assert.equal(record.online.inputDigest,old.digest);
  assert.deepEqual(current.snapshot.projectId,old.snapshot.projectId,'same durable project');
  for(const r of offline.trace)assert.equal(r.tag.digest,current.digest);
  assert.match(log,/Found 4 citekeys in bib section 0/,'genuine current BCF supplies all four citekeys to Biber');
  return {onlineRequest:online.request,offlineRequest:offline.request,onlineInputDigest:old.digest,offlineInputDigest:current.digest,
    runtimeIdentity:current.runtimeIdentity,sameInstalledRuntime:true,projectId:current.snapshot.projectId,
    onlineCapture:old.capture,offlineCapture:current.capture,productionEvidence:'read-only existing native bridge tags + UI input digest',
    fingerprintKind:'captured project content digest; internal BCF/backend control fingerprint is not exposed',backendControlFingerprintExposed:false,
    expectedBibliographyEntries:4,bblEvidence:'genuine Biber four-citekey/BBL-writing log plus subsequent successful TeX, four unique rendered bibliography markers and resolved fourth citation; no direct BBL accessor'};
}
async function readProductionNativePhases(page) {
  const details=await H.$(page,'compile-details').textContent(),request=details.match(/Request: ([0-9a-f]{32});/)?.[1];assert.ok(request);
  const observations=await page.evaluate(async()=>{
    const bridge=await import('./js/compile-port-bridge.js');return typeof bridge.observations==='function'?bridge.observations():null;
  });
  assert.ok(observations&&Array.isArray(observations.trace),'existing real worker trace is available');
  return {details,request,trace:observations.trace.filter(r=>r.tag.request===request)};
}
function assertPackageCompilerTrace(details,trace,compilerChoice,expectedRuntimeIdentity) {
  const expectedProfile=renderedReview.identityProfile(expectedRuntimeIdentity);
  const request=details.match(/Request: ([0-9a-f]{32});/)?.[1],digest=details.match(/Input: ([0-9a-f]{64})/)?.[1],epoch=Number(details.match(/epoch: (\d+)/)?.[1]);
  assert.ok(request&&digest&&Number.isSafeInteger(epoch)&&epoch>0);assert.match(expectedRuntimeIdentity,/^[0-9a-f]{64}$/);
  const [engineId,version]={'local-authoring-v4':['pdftex',23],'latex-dvi':['latex-dvi',24],xelatex:['xelatex',21],lualatex:['lualatex',22]}[compilerChoice];
  const tex=trace.filter(r=>r.tag.tool==='latex'),bib=trace.filter(r=>r.tag.tool==='bibtex'),converted=trace.filter(r=>r.tag.tool==='dvipdfmx');
  assert.ok(tex.length>=3&&tex.length<=6,'actual successful bounded TeX phase count');assert.ok(bib.length>=1,'actual selected BibTeX phase required');
  assert.equal(trace[0].tag.tool,'latex','complete current trace starts with TeX');
  const first=trace[0].tag,nonces=new Set();assert.match(first.job,/^[0-9a-f]{32}$/);
  for(const [i,r]of trace.entries()){
    assert.ok(r.posted&&r.entered&&r.terminated);assert.equal(r.outcome,'native-return');assert.equal(r.nativeReturn,0);
    assert.ok(['latex','bibtex','dvipdfmx'].includes(r.tag.tool),'no Biber or unselected backend substitution');
    assert.equal(r.tag.request,request);assert.equal(r.tag.digest,digest);assert.equal(r.tag.epoch,epoch);
    assert.equal(r.tag.profile,expectedProfile);assert.equal(r.tag.version,version);assert.equal(r.tag.engineId,engineId);
    assert.equal(r.tag.runtimeIdentity,expectedRuntimeIdentity,'phase uses the exact supplied authenticated P13/P14/P15 runtime');
    assert.equal(r.tag.job,first.job);assert.equal(r.tag.capture,first.capture);assert.deepEqual(r.tag.snapshot,first.snapshot);
    assert.ok(r.tag.snapshot&&r.tag.snapshot.captureId===r.tag.capture,'phase snapshot capture is correlated');
    assert.equal(r.tag.ordinal,i+1,'complete contiguous current-phase ordering');
    assert.match(r.tag.nonce,/^[0-9a-f]{32}$/);assert.ok(!nonces.has(r.tag.nonce),'distinct native phase nonce');nonces.add(r.tag.nonce);
    if(r.tag.tool==='bibtex'){
      assert.equal(trace[i-1]?.tag.tool,'latex','one selected backend after a genuine TeX pass');
      assert.equal(trace[i+1]?.tag.tool,'latex','genuine TeX consumes every successful backend output');
    }
  }
  assert.ok(tex.at(-1).tag.ordinal>bib.at(-1).tag.ordinal,'final TeX consumes successful BibTeX output');
  assert.equal(converted.length,['latex-dvi','xelatex'].includes(compilerChoice)?1:0,'only selected DVI/XDV engine converts once');
  assert.equal(trace.at(-1).tag.tool,converted.length?'dvipdfmx':'latex','conversion follows the final TeX pass');
  return {request,inputDigest:digest,runtimeIdentity:expectedRuntimeIdentity,engineId,profile:expectedProfile,texPhases:tex.length,bibtexPhases:bib.length,
    lastTexAfterLastBibtex:true,selectedBibliographyTool:'Bibtex',trace,source:'read-only existing production bridge observations',bblBytesExposed:false};
}
async function observePackageCompilerPhases(page) {
  assert.ok(PACKAGE_COMPILER_ONLY);
  assert.equal(await H.$(page,'bibliography-tool').inputValue(),'Bibtex','explicit selected UI backend');
  assert.equal(await page.locator('select[aria-label="Compile engine"]').inputValue(),COMPILER_CHOICE);
  const {details,trace}=await readProductionNativePhases(page);
  const runtime=renderedReview.classifyRuntime(pins);assert.ok(runtime,'only exact closed P13/P14 semantic runtimes');
  return assertPackageCompilerTrace(details,trace,COMPILER_CHOICE,runtime.identity);
}
async function observeR1Phases(page) {
  const runtime=renderedReview.classifyRuntime(pins);assert.ok(runtime,'only exact closed P13/P14 semantic runtimes');
  const {request,trace}=await readProductionNativePhases(page),tex=trace.filter(r=>r.tag.tool==='latex'),bib=trace.filter(r=>r.tag.tool==='biber');
  assert.ok(tex.length>=(BIBER_CASE?3:2),'actual successful TeX phase count');
  assert.ok(tex.length<=6,'actual bounded TeX phase count');
  const [expectedEngine,expectedPhase]={'local-authoring-v4':['pdftex',23],'latex-dvi':['latex-dvi',24],xelatex:['xelatex',21],lualatex:['lualatex',22]}[COMPILER_CHOICE];
  for(const r of trace){assert.ok(r.posted&&r.entered&&r.terminated);assert.equal(r.outcome,'native-return');assert.equal(r.nativeReturn,0);
    assert.equal(r.tag.profile,runtime.profile);assert.equal(r.tag.runtimeIdentity,runtime.identity);assert.equal(r.tag.engineId,expectedEngine);
    assert.equal(r.tag.version,r.tag.tool==='biber'?10:expectedPhase);}
  assert.ok(trace.every(r=>r.tag.tool!=='bibtex'),'no unexpected BibTeX substitution');
  if(BIBER_CASE){assert.ok(bib.length>=1,'genuine Biber phase required');for(const r of bib){assert.equal(r.tag.version,10);assert.equal(r.tag.profile,runtime.profile);}
    assert.ok(tex.at(-1).tag.ordinal>bib.at(-1).tag.ordinal,'real TeX consumes Biber result after phase10');}
  if(CYRILLIC_CASE)for(const r of tex){assert.equal(r.tag.profile,runtime.profile);assert.equal(r.tag.version,COMPILER_CHOICE==='latex-dvi'?24:23);}
  if(HARFBUZZ_CASE)for(const r of tex){assert.equal(r.tag.profile,runtime.profile);assert.equal(r.tag.version,22);assert.equal(r.tag.engineId,'lualatex');}
  return {request,trace,profile:runtime.profile,runtimeIdentity:runtime.identity,source:'read-only existing production bridge observations',bblBytesExposed:false};
}
function assertR1Text(text,biberEdited=false) {
  const norm=text.replace(/\s+/g,' ').trim(),markers=BIBER_CASE?[...biberManifest.pageMarkers,...biberManifest.bibliographyMarkers]
    :semanticsManifest.queries[HARFBUZZ_CASE?'harfbuzz.tex':CYRILLIC_FIXTURE];
  for(const literal of markers)assert.ok((BIBER_CASE?norm:text).includes(literal),'actual literal R1 PDF text '+literal);
  if(BIBER_CASE)assert.doesNotMatch(norm,/\?\?|\[(?:latin-book|ukrainian-book|latin-article)\]/,'resolved references and citations');
  if(BIBER_CASE&&biberEdited){
    for(const marker of [...biberManifest.bibliographyMarkers,BIBER_COLD_EDIT.marker])assert.equal(norm.split(marker).length-1,1,'four distinct rendered bibliography entries: '+marker);
    assert.ok(norm.includes(BIBER_COLD_EDIT.renderedCitation),'new fourth citation resolves to [4]');
    assert.doesNotMatch(norm,/\[offline-added-book\]/,'new citation cannot remain an unresolved citekey');
    markers.push(BIBER_COLD_EDIT.marker,BIBER_COLD_EDIT.renderedCitation);
  }
  if(CYRILLIC_CASE){
    for(const literal of markers)assert.equal(text.split(literal).length-1,CYRILLIC_FIXTURE==='mixed-semantic.tex'&&literal==='ASCII I i.'?4:2,'regular/bold original semantic occurrences '+literal);
    if(CYRILLIC_FIXTURE==='mixed-semantic.tex')for(const wrong of ['ASCII І i.','ASCII I і.','ASCII І і.'])assert.ok(!text.includes(wrong),'Latin controls cannot be replaced with Cyrillic');
  }
  return {markers,whitespaceNormalizationOnly:BIBER_CASE,unicodeNormalizationApplied:false,text:text.slice(0,8192)};
}
async function verifyLiteralR1Search(page, project, pdfHash, biberEdited=false) {
  const before=await H.head(page,project),rows=[],queries=BIBER_CASE?[...biberManifest.bibliographyMarkers,...(biberEdited?[BIBER_COLD_EDIT.marker]:[])]:semanticsManifest.queries[HARFBUZZ_CASE?'harfbuzz.tex':CYRILLIC_FIXTURE];
  for(const query of queries){
    await H.$(page,'pdf-pane').locator('input[aria-label="Search PDF"]').fill(query);
    const generation=await H.$(page,'pdf-pane').getAttribute('data-generation');
    await page.getByRole('button',{name:'Search document',exact:true}).click();
    await page.waitForFunction(old=>document.querySelector('[data-testid=pdf-pane]')?.dataset.generation!==old,generation,{timeout:30000});
    await waitPreviewAvailable(page,90000);
    await page.waitForFunction(()=>['Complete','Empty','Error','Cancelled'].includes(document.querySelector('[data-testid=search-status]')?.dataset.searchStatus),null,{timeout:30000});
    const status=H.$(page,'search-status');assert.equal(await status.getAttribute('data-search-status'),'Complete');
    const matches=Number(await status.getAttribute('data-match-count'));assert.ok(matches>0,'literal original PDF search '+query);
    assert.equal(await H.$(page,'pdf-pane').getAttribute('data-document'),pdfHash);rows.push({query,matches,normalizationApplied:false});
  }
  assert.deepEqual(await H.head(page,project),before,'literal search preserves all project bytes');return rows;
}
async function inspectR1Download(filename,biberEdited=false) {
  // Independent test invocation of the actual pinned E1 ABI over downloaded bytes.
  const loader=path.join(PDF_DIR,'files',loaderResource.name),wasm=fs.readFileSync(path.join(PDF_DIR,'files',wasmResource.name));
  const m=await require(loader)({wasmBinary:wasm,print(){},printErr(){}});
  const ok=(n,...a)=>assert.equal(m['_'+n](...a),0,n);
  const input=b=>{const h=m._e1_alloc(b.length);assert.ok(h);m.HEAPU8.set(b,m._e1_data(h));return h;};
  const take=()=>{const h=m._e1_result(),n=m._e1_size(h),d=input(Buffer.alloc(n));ok('e1_copy',h,d);const b=Buffer.from(m.HEAPU8.slice(m._e1_data(d),m._e1_data(d)+n));ok('e1_release',d);ok('e1_release',h);return b;};
  const raw=Buffer.alloc(36);[1,36,8388608,32,100000,256,4000000,8388608,64].forEach((n,i)=>raw.writeUInt32LE(n,i*4));
  const bytes=fs.readFileSync(path.join(RESULT_DIR,filename)),data=input(bytes),budget=input(raw);ok('e1_open',data,budget);const doc=m._e1_result();ok('e1_release',data);ok('e1_release',budget);
  const pages=m._e1_document_pages(doc);assert.ok(pages>0&&pages<=32);const texts=[],search=[];
  const queries=BIBER_CASE?[...biberManifest.bibliographyMarkers,...(biberEdited?[BIBER_COLD_EDIT.marker]:[])]:semanticsManifest.queries[HARFBUZZ_CASE?'harfbuzz.tex':CYRILLIC_FIXTURE];
  for(let i=0;i<pages;i++){ok('e1_page',doc,i);const page=m._e1_result();ok('e1_text',page);texts.push(JSON.parse(take()).text);
    for(const query of queries){const q=input(Buffer.from(query+'\0','utf16le'));ok('e1_search',page,q);const hits=JSON.parse(take());ok('e1_release',q);if(hits.length)search.push({query,page:i+1,matches:hits.length});}
    ok('e1_close',page);
  }
  ok('e1_close',doc);assert.equal(m._e1_live(),0);for(const query of queries)assert.ok(search.some(r=>r.query===query),'actual downloaded PDF literal search '+query);
  return {filename,length:bytes.length,sha256:sha256(bytes),pages,rendererWasmSha256:wasmResource.sha256,textEvidence:assertR1Text(texts.join('\n'),biberEdited),literalSearch:search};
}

async function downloadAcceptedPdf(page, project, label, filename, expectedHash) {
  phase = label;
  const headBefore = await H.head(page, project), editorBefore = await H.$(page, "editor").inputValue();
  const compileBefore = await H.$(page, "compile-details").textContent();
  const currentBefore = await H.$(page, "compile-panel").getAttribute("data-is-current");
  assert.equal(await H.$(page, "compile-panel").getAttribute("data-output-hash"), expectedHash);
  assert.equal(await H.$(page, "download-pdf").isEnabled(), true, "real PDF download is enabled");
  const nativeBefore = nativeWorkersInPhase(label).length;
  const totalNativeBefore = workers.filter(worker => NATIVE_WORKER_RE.test(worker.url)).length;
  await page.evaluate(() => {
    if (window.__fontDownloadMime) throw new Error("Previous MIME observer not restored");
    const descriptor = Object.getOwnPropertyDescriptor(URL, "createObjectURL");
    const original = descriptor.value;
    const records = [];
    window.__fontDownloadMime = { records, restore: () => Object.defineProperty(URL, "createObjectURL", descriptor) };
    Object.defineProperty(URL, "createObjectURL", { ...descriptor, value: function (...args) {
      const actualUrl = original.apply(this, args);
      if (args[0] instanceof Blob && records.length < 8) records.push({ type: args[0].type, size: args[0].size });
      return actualUrl; // Real URL unchanged. Never fetch a blob URL or replace a product response.
    } });
  });
  try {
    const pending = page.waitForEvent("download", { timeout: 30000 });
    pending.catch(() => {}); // Observe if a failed click exits before awaiting the event.
    await H.$(page, "download-pdf").click();
    const download = await pending;
    await download.saveAs(path.join(RESULT_DIR, filename));
    assert.equal(await download.failure(), null);
    const bytes = fs.readFileSync(path.join(RESULT_DIR, filename));
    assert.ok(bytes.length > 5 && bytes.subarray(0, 5).equals(Buffer.from("%PDF-")), "actual downloaded PDF bytes");
    assert.equal(sha256(bytes), expectedHash, "download bytes match actual displayed compiled PDF hash");
    const suggestedName = download.suggestedFilename();
    assert.match(suggestedName, /^[A-Za-z0-9][A-Za-z0-9._ -]{0,190}\.pdf$/i, "portable basename.pdf suggestion");
    assert.equal(path.basename(suggestedName), suggestedName);
    const mime = await page.evaluate(() => window.__fontDownloadMime.records);
    assert.ok(mime.some(item => item.type === "application/pdf" && item.size === bytes.length), "real original createObjectURL received PDF MIME and matching length");
    await page.waitForFunction(() => !!document.querySelector('[data-testid=pdf-download-status]')?.textContent?.trim(), null, { timeout: 10000 });
    const notice = await H.$(page, "pdf-download-status").textContent();
    assert.doesNotMatch(notice, /download failed|could not|unable to|no PDF is available/i);
    assert.deepEqual(await H.head(page, project), headBefore, "download does not save/change durable source head or font bytes");
    assert.equal(await H.$(page, "editor").inputValue(), editorBefore, "download does not change working source");
    assert.equal(await H.$(page, "compile-details").textContent(), compileBefore, "download does not request compilation");
    assert.equal(await H.$(page, "compile-panel").getAttribute("data-is-current"), currentBefore, "download does not promote stale PDF");
    assert.equal(await H.$(page, "compile-panel").getAttribute("data-output-hash"), expectedHash, "download keeps last displayed PDF identity");
    assert.equal(nativeWorkersInPhase(label).length, nativeBefore, "download phase starts no native compiler/PDF workers");
    assert.equal(workers.filter(worker => NATIVE_WORKER_RE.test(worker.url)).length, totalNativeBefore);
    return { file: filename, bytes: bytes.length, sha256: sha256(bytes), suggestedName, mime,
      notice: notice.slice(0, 512), nativeWorkersBefore: nativeBefore, nativeWorkersAfter: nativeWorkersInPhase(label).length,
      totalNativeWorkersBefore: totalNativeBefore, totalNativeWorkersAfter: totalNativeBefore, current: currentBefore,
      editorAndHeadUnchanged: true, compiledOutputUnchanged: true };
  } finally {
    await page.evaluate(() => { if (window.__fontDownloadMime) { window.__fontDownloadMime.restore(); delete window.__fontDownloadMime; } });
  }
}

let context;
const workers = [];
let renderedReviewWorkerStart=0;
// Native compile/PDF workers are the SW-synthesized internal entry URLs only
// (/__oop/native-worker/compile|pdf). An unrelated analyzer worker (e.g. a
// blob: nested worker) is permitted and must not be counted as a native
// compile/PDF worker in the install zero-worker assertions.
const NATIVE_WORKER_RE = /\/__oop\/native-worker\/(?:compile|pdf|synctex|dvi|xetex|luatex|biber)\b/;
const nativeWorkersInPhase = (p) => workers.filter((w) => w.phase === p && NATIVE_WORKER_RE.test(w.url));
const externalAttempts = [];
const externalSuccesses = [];
const errors = [];
const violations = [];

async function launch(offline) {
  renderedReviewWorkerStart=workers.length;
  context = await chromium.launchPersistentContext(profile, { headless: true, offline: !!offline });
  await context.route("**/*", (route) => {
    if (parseUrl(route.request().url()).origin === base) return route.continue();
    externalAttempts.push("external attempt");
    return route.abort();
  });
  await context.addInitScript(() => {
    window.__authorCsp = [];
    addEventListener("securitypolicyviolation", (e) => window.__authorCsp.push(e.effectiveDirective));
  });
  context.on("page", (page) => {
    page.on("worker", (worker) => workers.push({ phase, url: worker.url() }));
    page.on("pageerror", () => errors.push("pageerror"));
  });
  context.on("response", (response) => {
    if (parseUrl(response.url()).origin !== base && response.ok()) externalSuccesses.push("external success");
  });
  const page = await context.newPage();
  page.setDefaultTimeout(60000);
  return page;
}

let profile, base;
(async () => {
  profile = fs.mkdtempSync(path.join(os.tmpdir(), "local-authoring-profile-"));
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(PORT, "127.0.0.1", resolve);
  });
  base = `http://127.0.0.1:${PORT}`;

  try {
    // ===================== ONLINE PHASE =====================
    phase = "online";
    let page = await launch(false);
    const response = await page.goto(base);
    validateCsp(await response.text(), cspHeader); // served bytes, not source
    await H.ready(page);

    const project = `LocalAuthoring-${CASE}-${Date.now()}`;
    await H.reveal(page, page.locator("#new-project"));
    await page.locator("#new-project").fill(project);
    await page.getByRole("button", { name: "Create project", exact: true }).click();

    // Import fixture through the real single-file import picker.
    if (PDF_DOWNLOAD_CHECKS || PACKAGE_CASE || FINAL_R1_CASE) {
      assert.equal(await H.$(page, "download-pdf").isDisabled(), true, "no output: PDF download disabled");
      record.online.initialDownloadDisabled = true;
    }
    let expectedMainBytes;
    let importedCount = 1;
    if (CASE === "plain") {
      expectedMainBytes = MAJOR_HEADING_MAIN ?? (WORK_PAGE_NUMBER_CHECKS ? workPageNumbers.fixture(PLAIN_MAIN) : PLAIN_MAIN);
      await H.$(page, "import-input").setInputFiles({ name: "main.tex", mimeType: "text/plain", buffer: expectedMainBytes });
    } else if (['t2a-bundled','font-closure','unicode-fonts'].includes(CASE)) {
      await H.$(page, 'import-input').setInputFiles({name: 'main.tex', mimeType: 'text/plain', buffer: bundledT2a});
      expectedMainBytes = bundledT2a; importedCount = 1;
    } else if (CYRILLIC_CASE || HARFBUZZ_CASE) {
      for(const input of semanticFiles){
        const imported={...input,name:path.posix.basename(input.name)};
        await H.$(page,'import-input').setInputFiles(imported);
        if(imported.name!==input.name){
          await page.locator('[data-testid=select-file]').filter({hasText:imported.name}).first().click();
          await H.reveal(page,H.$(page,'rename-input'));await H.$(page,'rename-input').fill(input.name);await H.$(page,'rename').click();
          await page.locator('.editor-toolbar label',{hasText:input.name}).waitFor();
        }
      }
      expectedMainBytes=semanticFiles[0].buffer;importedCount=semanticFiles.length;
    } else if (['portable', 'packages', 'biblatex'].includes(FIXTURE_CASE) || BIBER_CASE) {
      for (const input of portableFiles) await H.$(page, 'import-input').setInputFiles(input);
      expectedMainBytes = portableFiles[0].buffer; importedCount = portableFiles.length;
    } else if (CASE === "golden") {
      await H.$(page, "import-input").setInputFiles({ name: "main.tex", mimeType: "text/plain", buffer: goldenMain });
      await H.$(page, "import-input").setInputFiles({ name: "chapter.tex", mimeType: "text/plain", buffer: goldenChapter });
      await H.$(page, "import-input").setInputFiles({ name: "references.bib", mimeType: "text/plain", buffer: goldenRefs });
      expectedMainBytes = goldenMain;
      importedCount = 3;
    } else if (CASE === "font-assets") {
      assert.equal(await H.$(page, "download-pdf").isDisabled(), true, "no output: PDF download disabled");
      record.online.initialDownloadDisabled = true;
      for (const file of EFFECTIVE_FONT_FILES) {
        await H.$(page, "import-input").setInputFiles({ name: file.name, mimeType: file.mimeType, buffer: file.buffer });
      }
      expectedMainBytes = EFFECTIVE_FONT_FILES.find(file => file.name === "main.tex").buffer;
      importedCount = EFFECTIVE_FONT_FILES.length;
      assert.equal(importedCount, 7);
    } else {
      const fix = longFixture();
      await H.$(page, "import-input").setInputFiles({ name: "main.tex", mimeType: "text/plain", buffer: fix.main });
      for (const ch of fix.chapters) {
        await H.$(page, "import-input").setInputFiles({ name: ch.name, mimeType: "text/plain", buffer: ch.buffer });
      }
      expectedMainBytes = fix.main;
      importedCount = 1 + fix.chapters.length; // 64
      assert.equal(importedCount, 64, "long fixture imports exactly 64 files");
    }
    if (MAIN_PATH !== "main.tex") {
      await page.locator('[data-testid="select-file"]', {hasText:"main.tex"}).first().click();
      await H.reveal(page, H.$(page, "rename-input"));
      await H.$(page, "rename-input").fill(MAIN_PATH);
      await H.$(page, "rename").click();
      await page.locator(".editor-toolbar label", {hasText:MAIN_PATH}).waitFor();
    }
    // Select the exact renamed source; native input is never aliased.
    await selectMainInTree(page);
    await H.save(page);
    const savedHead = await H.head(page, project);
    record.online.project = project;
    record.online.importedFiles = importedCount;
    record.online.sourceSha256 = sha256(Buffer.from(savedHead.files.find((f) => f.path === MAIN_PATH).bytes));
    assert.equal(record.online.sourceSha256, sha256(expectedMainBytes), "saved main.tex bytes match imported");
    if (PACKAGE_CASE) record.online.packageInputs = assertPackageHead(savedHead, expectedMainBytes);
    if (FINAL_R1_CASE) record.online.capturedInputs=assertCapturedR1Head(savedHead,expectedMainBytes);
    if (CASE === "font-assets") record.online.projectAssets = assertFontHead(savedHead, expectedMainBytes);

    if (BUNDLED_RUNTIME) {
      phase = "automatic-compiler-preparation";
      await waitReadiness(page, true, 300000);
      await H.reveal(page, H.$(page, "bundled-runtime-info"));
      assert.equal(await H.$(page, "runtime-zip-input").count(), 0, "no manual compiler installation UI");
      assert.equal(await H.$(page, "pdf-runtime-manifest").count(), 0, "no manual renderer installation UI");
      assert.deepEqual(await page.locator('select[aria-label="Compile engine"] option').evaluateAll(options => options.map(o => o.value).filter(Boolean)),
        ["local-authoring-v4", "latex-dvi", "xelatex", "lualatex"]);
      assert.equal(nativeWorkersInPhase("automatic-compiler-preparation").length, 0, "preparation does not execute a compiler");
      record.online.automaticBundledTools = true;
      record.online.readinessReady = true;
    } else {
      // --- Install compiler resources; prove no native worker during install.
      phase = "install-compiler";
      await H.reveal(page, H.$(page, "runtime-recheck"));
      const beforeRecheck = Number(await H.$(page, 'runtime-status').getAttribute('data-operation'));
      await H.$(page, "runtime-recheck").click();
      await runtimeState(page, "Empty", 60000, beforeRecheck);
      assert.equal(await H.$(page, "compile").isDisabled(), true);
      assert.match(await H.$(page, "compiler-readiness").textContent(), /Compiler unavailable/);
      const beforeInstallation = Number(await H.$(page, 'runtime-status').getAttribute('data-operation'));
      if (suite) {
        await H.$(page, 'runtime-zip-input').setInputFiles(path.join(RUNTIME_DIR, 'compiler-suite.zip'));
        await H.$(page, 'runtime-zip-install').click();
      } else {
        await H.$(page, "runtime-pins").setInputFiles(pinsPath);
        await H.$(page, "runtime-files").setInputFiles(compilerFiles);
        await H.$(page, "runtime-install").click();
    }
    await runtimeState(page, "Installed", suite ? 300000 : 180000, beforeInstallation);
    assert.equal(
      (await H.$(page, "runtime-status").textContent()).trim(),
      "Compiler resources installed. Use Check tools to verify local compilation availability.",
    );
    assert.deepEqual(
      nativeWorkersInPhase("install-compiler"),
      [],
      "installing compiler resources must not spawn a native worker",
    );

    // --- Install PDF resources; prove no native worker during install.
    phase = "install-pdf";
    await H.reveal(page, H.$(page, "pdf-runtime-recheck"));
    await H.$(page, "pdf-runtime-recheck").click();
    await pdfRuntimeState(page, "Empty", 60000);
    assert.match(await H.$(page, "renderer-readiness").textContent(), /Preview unavailable/);
    await H.$(page, "pdf-runtime-manifest").setInputFiles(pdfManifestPath);
    await H.$(page, "pdf-runtime-files").setInputFiles(pdfFiles);
    await H.$(page, "pdf-runtime-install").click();
    await pdfRuntimeState(page, "Installed", 120000);
    assert.equal(
      (await H.$(page, "pdf-runtime-status").textContent()).trim(),
      "PDF resources installed. Preview separately verifies the supported E1 renderer when used.",
    );
    assert.deepEqual(
      nativeWorkersInPhase("install-pdf"),
      [],
      "installing PDF resources must not spawn a native worker",
    );

    // --- Check tools: real readiness re-read over installed resources.
    phase = "check-tools";
    // H.reveal opens ANCESTOR details of the target, not the target itself:
    // reveal the refresh-compile-tools button (a descendant) so its ancestor
    // compiler-settings <details> opens.
    await H.reveal(page, H.$(page, "refresh-compile-tools"));
    await H.$(page, "refresh-compile-tools").click();
    await waitReadiness(page, true, 60000);
    record.online.readinessReady = true;

    }

    // --- Select main.tex (tree) and the supported local-authoring-v4 engine.
    await selectMainInTree(page);
    await page.locator('select[aria-label="Compile main file"]').selectOption({ label: MAIN_PATH });
    await page.locator('select[aria-label="Compile engine"]').selectOption(COMPILER_CHOICE);
    if (["golden", "portable", "packages", "biblatex"].includes(FIXTURE_CASE)) {
      await H.bibliography(page, "Bibtex");
    }
    if (["font-assets", "t2a-bundled", "font-closure", "unicode-fonts"].includes(CASE)) {
      await H.bibliography(page, "None");
    }
    if (BIBER_CASE) await H.bibliography(page, 'Biber');
    if (CYRILLIC_CASE || HARFBUZZ_CASE) await H.bibliography(page, 'None');
    await page.waitForFunction(
      () => document.querySelector("[data-testid=compile]")?.disabled === false,
      null,
      { timeout: 20000 },
    );

    // --- Compile through the registered production lifecycle.
    phase = "compile";
    await H.$(page, "compile").click();
    record.online.compileElapsedMs = await waitCompileDone(page, suite ? 150000 : 90000);
    const compileStatus = await H.$(page, "compile-status").textContent();
    assert.ok(/^Compilation finished\./.test(compileStatus), `compile did not finish: ${compileStatus}`);
    record.online.compileUnderAppDeadline = record.online.compileElapsedMs < (suite ? 120000 : 30000);
    record.online.compileUnderNativeDeadline = record.online.compileElapsedMs < (suite ? 90000 : 25000);

    const panel = H.$(page, "compile-panel");
    const outputHash = await panel.getAttribute("data-output-hash");
    const isCurrent = await panel.getAttribute("data-is-current");
    assert.ok(outputHash && /^[0-9a-f]{64}$/.test(outputHash), "displayed PDF hash");
    assert.equal(isCurrent, "true", "displayed PDF is current");
    record.online.outputHash = outputHash;
    assert.equal((await H.$(page, "compiled-pdf-status").textContent()).trim(), "PDF up to date");
    const detailsText = await H.$(page, "compile-details").textContent();
    // CompileRequestId.ToString() is Value.ToString("N"): 32 lower-hex, no 0x prefix.
    assert.match(detailsText, /Request: [0-9a-f]{32};/);
    assert.match(detailsText, /epoch: \d+/);
    record.online.workerEpoch = parseInt((detailsText.match(/epoch: (\d+)/) || [])[1] || "0", 10);
    record.online.inputDigest = (detailsText.match(/Input: ([0-9a-f]{64})/) || [])[1] || null;
    record.online.pdfHashFromDetails = (detailsText.match(/PDF: ([0-9a-f]{64})/) || [])[1] || null;
    const logText = await H.$(page, "compiler-log").textContent();
    assert.ok(logText && logText.length > 0, "compiler log present");
    if (CASE === "font-assets") {
      assert.match(detailsText, /Outcome: Succeeded/);
      record.online.actualCompilerLog = logText.slice(0, 16384);
      record.online.fontLogEvidence = assertFontLog(logText);
    }
    record.online.nativeWorkersDuringCompile = nativeWorkersInPhase("compile").length;
    if (["latex-dvi", "xelatex"].includes(COMPILER_CHOICE)) {
      record.online.converterWorkers = nativeWorkersInPhase("compile").filter(worker => /\/dvi\?/.test(worker.url)).length;
      assert.equal(record.online.converterWorkers, 1, "one real final-pass DVI conversion");
    }

    // --- PDF preview: actual page data + nonblank canvas + text from renderer.
    phase = "preview";
    if (CASE === "long") {
      // Attempt the real render; do NOT force a 32-page cap or always skip.
      try { await waitPreviewAvailable(page, 90000); } catch { /* fall to status check */ }
      const status = await previewStatus(page);
      const paneDoc = await H.$(page, "pdf-pane").getAttribute("data-document");
      if (paneDoc && status === "Available") {
        const n = await pageCount(page);
        record.online.pageCount = n;
        // Verify actual first and last page via UI navigation (bounded).
        await goToPage(page, 1);
        assert.ok((await canvasNonwhite(page)) > 0, "long page 1 raster");
        await goToPage(page, n);
        assert.ok((await canvasNonwhite(page)) > 0, `long last page ${n} raster`);
        // Bounded full-document search; long docs report Partial (search cap).
        await H.$(page, "pdf-pane").locator('input[aria-label="Search PDF"]').fill("Kyiv");
        await page.getByRole("button", { name: "Search document" }).click();
        // data-search-status is the enum ToString (TitleCase): Empty/Scanning/
        // Complete/Partial/Error/Cancelled. For n>32 the finite 32-page search
        // policy must report Partial, not merely a terminal value.
        await page.waitForFunction(
          () => ["Complete", "Partial", "Empty", "Error", "Cancelled"].includes(
            document.querySelector("[data-testid=search-status]")?.dataset.searchStatus || "",
          ),
          null,
          { timeout: 60000 },
        );
        const searchStatus = await H.$(page, "search-status").getAttribute("data-search-status");
        if (n > 32) assert.equal(searchStatus, "Partial", "long doc search bounded to 32 pages reports Partial");
        record.online.searchStatus = searchStatus;
        record.online.previewStatus = "Available";
        record.online.continuedSearch = await verifyLongSearch(page, project, "Kyiv end.", outputHash, "online-long-search");
        record.online.sectionRanges = await verifySectionRanges(page, project, outputHash, "online-section-ranges", false);
      } else {
        // The new 512 renderer pair is adopted as a coupled source change, so
        // the long fixture MUST render. An unavailable old renderer is an
        // explicit failure (recorded with phase + UI status), never an accepted
        // skip and never an overall PASS with a missing render.
        record.online.previewStatus = status || "unavailable";
        assert.fail(
          `long fixture preview unavailable (status=${status || "none"}): the 64-page renderer pair must render this document`,
        );
      }
    } else {
      await waitPreviewAvailable(page, 90000);
      const docHash = await H.$(page, "pdf-pane").getAttribute("data-document");
      assert.equal(docHash, outputHash, "preview document matches compiled PDF");
      record.online.pageCount = await pageCount(page);
      assert.ok(record.online.pageCount >= 1, "page count >= 1");
      assert.match(await H.$(page, "canvas-status").textContent(), /Drawn/);
      assert.ok((await canvasNonwhite(page)) > 0, "canvas nonblank raster");
      // Collect markers across actual pages (do not assume bibliography on page 1).
      const allText = await collectAllPageText(page);
      if (PACKAGE_CASE) {
        // Retain genuine bytes/text before strict text or bibliography assertions.
        record.online.actualPageText = allText.slice(0, 4096);
        assert.deepEqual(await H.head(page, project), savedHead, 'package compile/render preserve every saved input');
        record.online.download = await downloadAcceptedPdf(page, project, 'online-package-download', 'online.pdf', outputHash);
        if(PACKAGE_COMPILER_ONLY){record.online.finalTexPass=assertPackageCompilerLog(logText);record.online.realNativePhases=await observePackageCompilerPhases(page);}
      }
      if (FINAL_R1_CASE) {
        record.online.actualPageText=allText.slice(0,8192);
        assertCapturedR1Head(await H.head(page,project),expectedMainBytes);
        record.online.download=await downloadAcceptedPdf(page,project,'online-r1-download','online.pdf',outputHash);
        record.online.downloadInspection=await inspectR1Download('online.pdf');
        record.online.finalTexPass=assertFinalR1Log(logText);
        record.online.realNativePhases=await observeR1Phases(page);
        record.online.literalSearch=await verifyLiteralR1Search(page,project,outputHash);
        if(BIBER_CASE){assert.match(logText,/Found 3 citekeys in bib section 0/,'original BCF/BIB has exactly the original three citekeys');assert.ok(!allText.includes(BIBER_COLD_EDIT.marker),'offline entry is absent from the original online PDF');}
      }
      assert.ok(allText.trim().length > 0, "page text nonblank");
      record.online.pageTextLen = allText.trim().length;
      if (FINAL_R1_CASE) {
        record.online.textEvidence=assertR1Text(allText);
        if(HARFBUZZ_CASE)assert.match(logText,/This is LuaHBTeX, Version/);
      } else if (FONT_CLOSURE || UNICODE_FONTS) {
        record.online.fontCoverage = assertFontClosure(logText, allText);
      } else if (CASE === "plain") {
        // Accepted plain baseline markers: "Modern CSharp pipeline" and the
        // resolved "Section 1 is resolved." (normalize whitespace only).
        const norm = allText.replace(/\s+/g, " ").trim();
        assert.match(norm, /Modern CSharp pipeline/);
        assert.ok(norm.includes("Section 1 is resolved."), "plain section reference resolved");
      } else if (['portable', 'packages', 'biblatex'].includes(FIXTURE_CASE)) {
        const norm = allText.replace(/\s+/g, ' ').trim();
        assert.match(norm, /Portable compiler fixture/);
        assert.match(norm, /Section 2 is resolved\./);
        assert.match(norm, /Zoe Zimmer/);
        assert.match(norm, /Portable Compiler Reference/);
        if (['xelatex', 'lualatex'].includes(COMPILER_CHOICE)) {
          assert.match(norm, /Привіт/); assert.match(norm, /Київ/);
        }
        if (['packages', 'biblatex'].includes(FIXTURE_CASE)) {
          record.online.packageTextEvidence = sourcePdfNavigation.assertPackageText(allText, FIXTURE_CASE, COMPILER_CHOICE);
          assert.match(norm, /Hyperlink fixture/);
          assert.match(norm, /Listing marker/);
          assert.match(norm, /Package compatibility entry/);
          assert.doesNotMatch(logText, /Missing character:|LaTeX Font Warning:|not loadable|File[^\n]*not found/);
          if (['xelatex', 'lualatex'].includes(COMPILER_CHOICE)) {
            assert.match(logText, /COMPATIBILITY-UKRAINIAN-ID:[1-9][0-9]*/);
            assert.match(logText, /COMPATIBILITY-ENGLISH-ID:0\b/);
            assert.doesNotMatch(logText, /No hyphenation patterns were preloaded|Unknown language|Ukrainian hyphenation patterns are not loaded/i);
          }
        }
        record.online.actualPageText = allText.slice(0, 4096);
      } else if (CASE === 't2a-bundled') {
        const norm = allText.replace(/\s+/g, ' ').trim();
        assert.equal(norm.split('Україна').length - 1, 2, 'actual regular and bold bundled T2A text');
        assert.match(norm, /Єє Її Ґґ/);
        assert.match(norm, /ASCII I i 123\./);
        assert.match(logText, /COMPATIBILITY-UKRAINIAN-ID:[1-9][0-9]*/);
        assert.match(logText, /COMPATIBILITY-ENGLISH-ID:0\b/);
        assert.doesNotMatch(logText, /Missing character:|LaTeX Font Warning:|not loadable|File[^\n]*not found/);
        record.online.actualPageText = allText.slice(0, 4096);
        record.online.projectFontFiles = 0;
      } else if (CASE === "font-assets") {
        // Preserve diagnostic data and the REAL PDF before strict text assertions.
        // A font/Unicode mismatch must still fail, but cannot hide the oracle artifact.
        record.online.actualPageText = allText.slice(0, 2048);
        record.online.actualPageTextCodepoints = Array.from(record.online.actualPageText)
          .map(character => "U+" + character.codePointAt(0).toString(16).toUpperCase().padStart(4, "0"));
        record.online.actualPageTextTruncated = allText.length > 2048;
        assert.equal((await H.$(page, "pdf-hash").textContent()).trim(), outputHash, "font preview hash before artifact download");
        assert.equal(await panel.getAttribute("data-is-current"), "true");
        assert.deepEqual(await H.head(page, project), savedHead, "online compile/render preserve all7 saved project bytes");
        record.online.nativeWorkersDuringPreview = nativeWorkersInPhase("preview").length;
        record.online.download = await downloadAcceptedPdf(page, project, "online-font-download", "online-font.pdf", outputHash);
        assertFontHead(await H.head(page, project), expectedMainBytes);
        phase = "font-text";
        record.online.fontTextEvidence = assertFontPageText(allText);
        if (T2A_UNICODE_REPAIR) record.online.latinTextEvidence = assertRepairLatinText(allText);
      } else {
        assert.ok(allText.includes("Привіт"), "Ukrainian Привіт in page text");
        assert.ok(allText.includes("Київ"), "Ukrainian Київ in page text");
        assert.ok(
          allText.includes("Alice Adams") || allText.includes("Zoe Zimmer"),
          "bibliography entry in page text",
        );
      }
      assert.equal((await H.$(page, "pdf-hash").textContent()).trim(), outputHash, "pdf-hash matches output");
      record.online.nativeWorkersDuringPreview = nativeWorkersInPhase("preview").length;
    }

    if (PDF_DOWNLOAD_CHECKS && !PACKAGE_CASE && !FINAL_R1_CASE) {
      assert.deepEqual(await H.head(page, project), savedHead, "online plain compile/render preserve saved source");
      record.online.download = await downloadAcceptedPdf(page, project, "online-plain-download", "online.pdf", outputHash);
    }

    if (MAIN_PATH !== "main.tex") {
      assert.deepEqual(await H.head(page, project), savedHead, "selected-root compile preserves exact original saved files");
      record.online.selectedMain = MAIN_PATH;
      record.online.selectedDownload = await downloadAcceptedPdf(page, project, "selected-root-online-download", "selected-online.pdf", outputHash);
    }

    if (SOURCE_PDF_CHECKS) {
      phase = "online-source-pdf-navigation";
      record.online.sourcePdfNavigation = await sourcePdfNavigation.verify(page, project, MAIN_PATH, BIBER_CASE ? "portable" : CASE === "portable" || PACKAGE_CASE ? CASE : CASE === "golden", phase);
    }
    // --- Wait for the offline cache to be genuinely provisioned.
    if (WORK_PAGE_NUMBER_CHECKS) {
      phase = 'online-work-page-numbers';
      record.online.workPageNumbers = await workPageNumbers.verify(page, project, outputHash, () => workers, false);
    }
    if (WORK_MAJOR_HEADING_CHECKS) {
      phase = 'online-work-major-heading';
      record.online.workMajorHeading = await workMajorHeading.verify(page, project, outputHash, () => workers, renderedReviewPreflight.renderer, false);
    }
    if(RENDERED_REVIEW_CHECKS){
      phase='online-rendered-review';
      record.online.renderedReview=await renderedReview.verify(page,{project,hash:outputHash,pages:await pageCount(page),
        workers:()=>workers.slice(renderedReviewWorkerStart),expected:renderedReviewPreflight,offline:false,
        previewReady:()=>waitPreviewAvailable(page,90000),recompile:async()=>{
          phase='online-rendered-review-same-source-recompile';
          const previousRequest=(await H.$(page,'compile-details').textContent()).match(/Request: ([0-9a-f]{32});/)?.[1];assert.ok(previousRequest);
          const previousGeneration=Number(await H.$(page,'pdf-pane').getAttribute('data-generation'));
          await H.$(page,'compile').click();
          // A completed previous attempt is still visible until the click is
          // processed. Wait for a NEW accepted request and its actual draw.
          // Same-source recompiles may legitimately reproduce the same PDF hash.
          await page.waitForFunction(({request,generation})=>{
            const details=document.querySelector('[data-testid=compile-details]')?.textContent||'';
            const next=details.match(/Request: ([0-9a-f]{32});/)?.[1],pane=document.querySelector('[data-testid=pdf-pane]');
            return next&&next!==request&&document.querySelector('[data-testid=compile-panel]')?.dataset.isCurrent==='true'
              &&/^Compilation finished\./.test(document.querySelector('[data-testid=compile-status]')?.textContent||'')
              &&/^Idle/.test(document.querySelector('[data-testid=compile-work-status]')?.textContent||'')
              &&Number(pane?.dataset.generation)>generation&&pane?.dataset.document
              &&document.querySelector('[data-testid=preview-status]')?.dataset.previewStatus==='Available'
              &&/Drawn/.test(document.querySelector('[data-testid=canvas-status]')?.textContent||'');
          },{request:previousRequest,generation:previousGeneration},{timeout:suite?150000:90000});
          await waitPreviewAvailable(page,90000);phase='online-rendered-review-reconfirmed';
          return {hash:await H.$(page,'compile-panel').getAttribute('data-output-hash')};
        }});
    }
    await page.waitForFunction(() => window.__oopOfflineReady === true, null, { timeout: 90000 });
    record.online.offlineReady = true;
    violations.push(...(await page.evaluate(() => window.__authorCsp || [])));

    // ===================== COLD OFFLINE PROOF =====================
    // 1. Close EVERY Chromium process; record pid/startticks/executable census.
    await context.close();
    context = null;
    // Finite reap loop (bounded polling with setTimeout; not a no-sleep claim).
    let after = [];
    for (let i = 0; i < 40; i++) {
      after = chromiumCensus();
      if (after.length === 0) break;
      await new Promise((r) => setTimeout(r, 100));
    }
    record.coldOffline.chromiumCensusBeforeRelaunch = after;
    assert.equal(after.length, 0, "every Chromium process closed before offline relaunch");

    // 2. Stop the owned origin and prove ECONNREFUSED (bounded exit listener).
    await new Promise((resolve, reject) => {
      let done = false;
      server.close(() => { if (!done) { done = true; resolve(); } });
      setTimeout(() => { if (!done) { done = true; reject(new Error("origin close timeout")); } }, 5000);
    });
    await new Promise((resolve, reject) => {
      const r = http.get(base, () => reject(new Error("origin still accepts connections")));
      r.on("error", (e) => (e.code === "ECONNREFUSED" ? resolve() : reject(e)));
      r.setTimeout(2000, () => r.destroy(new Error("refusal not proven")));
    });
    record.coldOffline.originRefused = true;

    // 3. New browser, network disabled BEFORE first navigation; fresh process
    // identity (startticks) is the cross-boundary proof, not worker-epoch
    // inequality (counters may restart at 1 across separate JS lifetimes).
    const beforeRelaunchCensus = chromiumCensus();
    assert.equal(beforeRelaunchCensus.length, 0, "no Chromium before relaunch");
    phase = "cold-offline";
    page = await launch(true);
    const relaunchedCensus = chromiumCensus();
    assert.ok(relaunchedCensus.length > 0, "new Chromium process exists");
    record.coldOffline.newBrowserStartticks = relaunchedCensus[0].startticks;
    await page.goto(base);
    await H.ready(page);
    record.coldOffline.offlineBeforeNavigation = true;

    // 4. Reopen the same profile/project (persisted in IndexedDB/OPFS). The
    // cold root URL always opens the dashboard on a new app context. H.ready may
    // return once the dashboard shell exists BEFORE its async project rows
    // load, so an instantaneous project-row count() gate races and skips
    // opening. Unconditionally call H.open: its real row/Open locator
    // automatically waits for the async row and clicks normally. No
    // count/isVisible gate, no storage injection.
    await H.open(page, project);
    if(FINAL_R1_CASE)record.coldOffline.reopenedCapturedInputs=assertCapturedR1Head(await H.head(page,project),expectedMainBytes);
    if (CASE === "font-assets") {
      assert.deepEqual(await H.head(page, project), savedHead, "all7 source/font assets survive entire-browser cold restart");
      record.coldOffline.reopenedProjectAssets = assertFontHead(await H.head(page, project), expectedMainBytes);
    }
    // Once the workspace is open, reveal the refresh-compile-tools button's
    // ANCESTOR <details> (compiler-settings) FIRST — on a cold workspace it is
    // collapsed, so a visible() wait on the button before revealing can never
    // pass. H.reveal opens the real ancestor disclosure via summary clicks; do
    // not target compiler-settings itself (the helper only opens ancestors).
    await H.reveal(page, H.$(page, "refresh-compile-tools"));
    // Then wait for the control to be visible and enabled (bounded 60s),
    // allowing the app's initial stored-tool check to settle before explicit
    // installer rechecks. No automatic retries or changed product timeouts.
    if (BUNDLED_RUNTIME) {
      await waitReadiness(page, true, 180000);
      record.coldOffline.automaticBundledTools = true;
    } else {
      await H.$(page, "refresh-compile-tools").waitFor({ state: "visible", timeout: 60000 });
      await page.waitForFunction(
        () => document.querySelector("[data-testid=refresh-compile-tools]")?.disabled === false,
        null,
        { timeout: 60000 },
      );
      // Recheck both resource stores from local bytes (no fetch).
      await H.reveal(page, H.$(page, "runtime-recheck"));
      await H.$(page, "runtime-recheck").click();
      await runtimeState(page, "Installed", 60000);
      await H.reveal(page, H.$(page, "pdf-runtime-recheck"));
      await H.$(page, "pdf-runtime-recheck").click();
      await pdfRuntimeState(page, "Installed", 60000);

    }

    // 5. Select main.tex via tree; edit (visible change BEFORE \end{document}),
    // save, analyze locally. The offline compile must produce a NEW PDF.
    await selectMainInTree(page);
    const editorContent = await H.$(page, "editor").inputValue();
    assert.equal(editorContent, expectedMainBytes.toString("utf8"), "reopened source matches saved content");
    // Assert exactly one \end{document} token (independent of trailing LF;
    // golden Main has no trailing newline), insert a VISIBLE section before
    // that token, and preserve any original following newline.
    const token = "\\end{document}";
    assert.equal(editorContent.split(token).length - 1, 1, "exactly one \\end{document} token");
    const changedBaseSource = WORK_MAJOR_HEADING_CHECKS ? editorContent : T2A_UNICODE_REPAIR ? editorContent.replace(
      token,
      "% cold-offline fresh source marker\n" +
        "{\\fontencoding{OT1}\\selectfont\n\\noindent Edited offline proof: Kyiv 456. \\par}\n" + token,
    ) : ["font-assets", "t2a-bundled", "font-closure", "unicode-fonts"].includes(CASE) || CYRILLIC_CASE || HARFBUZZ_CASE ? editorContent.replace(
      token,
      "% cold-offline fresh source marker\n" +
        "\\noindent Edited offline proof: Kyiv 456. \\par\n" + token,
    ) : editorContent.replace(
      token,
      "% cold-offline fresh source marker\n" +
        "\\section{Edited offline}\\label{sec:edited}\n" +
        "Edited offline proof: Kyiv 456. \\par\n" +
        token,
    );
    let changedSource=changedBaseSource;
    if(BIBER_CASE){
      const bibliography='\\printbibliography';assert.equal(changedSource.split(bibliography).length-1,1,'one original bibliography print');
      changedSource=changedSource.replace(bibliography,BIBER_COLD_EDIT.citation+'\n'+bibliography);
      phase='cold-offline-bibliography-edit';
      record.coldOffline.changedBibliography=await editBiberBibliography(page,project,await H.head(page,project),expectedMainBytes);
    }
    if (!WORK_MAJOR_HEADING_CHECKS) {
      assert.ok(
        changedSource.indexOf("Edited offline") < changedSource.indexOf(token, changedSource.indexOf("Edited offline")),
        "offline marker precedes \\end{document} token",
      );
      phase='cold-offline-source-edit';
      await H.$(page, "editor").fill(changedSource);
      await H.save(page);
    }
    const offlineSavedHead = await H.head(page, project);
    if (WORK_MAJOR_HEADING_CHECKS) {
      assert.deepEqual(offlineSavedHead,savedHead,"unchanged heading fixture reopens at its exact saved revision");
      record.coldOffline.unchangedHeadingFixture = true;
    } else {
      record.coldOffline.editedHeadId = offlineSavedHead.id;
      record.coldOffline.editedSourceSha256 = sha256(Buffer.from(offlineSavedHead.files.find((f) => f.path === MAIN_PATH).bytes));
    }
    if (PACKAGE_CASE) record.coldOffline.packageInputs = assertPackageHead(offlineSavedHead, Buffer.from(changedSource, 'utf8'));
    if(FINAL_R1_CASE)record.coldOffline.capturedInputs=assertCapturedR1Head(offlineSavedHead,Buffer.from(changedSource,'utf8'),BIBER_CASE);
    if(BIBER_CASE)record.coldOffline.changedBibliography={...record.coldOffline.changedBibliography,...captureBiberEdit(offlineSavedHead,savedHead,Buffer.from(changedSource,'utf8'))};
    if (!WORK_MAJOR_HEADING_CHECKS)
      assert.notEqual(record.coldOffline.editedSourceSha256, record.online.sourceSha256, "source actually changed offline");
    await H.selectKnu(page);
    assert.deepEqual(await H.head(page, project), offlineSavedHead, "analyze did not mutate source");
    record.coldOffline.analyzed = true;
    if (CASE === "font-assets") record.coldOffline.editedProjectAssets = assertFontHead(offlineSavedHead, Buffer.from(changedSource, "utf8"));
    if (BUNDLED_RUNTIME) {
      await waitReadiness(page, true, 180000);
      record.coldOffline.readinessReadyOffline = true;
    } else {
      // 6. Check tools from installed resources (no fetch), then compile fresh.
      phase = "offline-check-tools";
      await H.reveal(page, H.$(page, "refresh-compile-tools"));
      await H.$(page, "refresh-compile-tools").click();
      await waitReadiness(page, true, 60000);
      record.coldOffline.readinessReadyOffline = true;

    }

    phase = "offline-compile";
    await selectMainInTree(page);
    await page.locator('select[aria-label="Compile main file"]').selectOption({ label: MAIN_PATH });
    await page.locator('select[aria-label="Compile engine"]').selectOption(COMPILER_CHOICE);
    if (["golden", "portable", "packages", "biblatex"].includes(FIXTURE_CASE)) {
      await H.bibliography(page, "Bibtex");
    }
    if (["font-assets", "t2a-bundled", "font-closure", "unicode-fonts"].includes(CASE)) {
      await H.bibliography(page, "None");
    }
    if (BIBER_CASE) await H.bibliography(page, 'Biber');
    if (CYRILLIC_CASE || HARFBUZZ_CASE) await H.bibliography(page, 'None');
    await page.waitForFunction(
      () => document.querySelector("[data-testid=compile]")?.disabled === false,
      null,
      { timeout: 20000 },
    );
    await H.$(page, "compile").click();
    record.coldOffline.compileElapsedMs = await waitCompileDone(page, suite ? 150000 : 90000);
    const offlineCompileStatus = await H.$(page, "compile-status").textContent();
    assert.ok(/^Compilation finished\./.test(offlineCompileStatus), `offline compile did not finish: ${offlineCompileStatus}`);
    const offlineOutputHash = await H.$(page, "compile-panel").getAttribute("data-output-hash");
    assert.equal(await H.$(page, "compile-panel").getAttribute("data-is-current"), "true", "offline PDF current");
    assert.ok(offlineOutputHash && /^[0-9a-f]{64}$/.test(offlineOutputHash), "offline displayed PDF hash");
    if (!WORK_MAJOR_HEADING_CHECKS) assert.notEqual(offlineOutputHash, outputHash, "fresh compile produced a new PDF (changed source)");
    record.coldOffline.outputHash = offlineOutputHash;
    if (CASE === "font-assets") {
      assert.match(await H.$(page, "compile-details").textContent(), /Outcome: Succeeded/);
      const offlineLog = await H.$(page, "compiler-log").textContent();
      record.coldOffline.actualCompilerLog = offlineLog.slice(0, 16384);
      record.coldOffline.fontLogEvidence = assertFontLog(offlineLog);
    }

    // 7. Render the fresh PDF offline; verify current doc hash + nonblank raster
    // + the edited marker. Long MUST render offline (new 512 pair is adopted as
    // a coupled source change); use first+last page only, check the Edited
    // offline visible marker on the last/newlast page. Overall long case may
    // never report PASS with a missing offline render. plain/golden use bounded
    // actual page marker collection. New worker construction (same-session
    // request/output identity, not cross-session epoch inequality).
    await waitPreviewAvailable(page, 90000);
    const offlineDoc = await H.$(page, "pdf-pane").getAttribute("data-document");
    assert.equal(offlineDoc, offlineOutputHash, "offline preview matches fresh PDF");
    record.coldOffline.nativeWorkersDuringCompile = nativeWorkersInPhase("offline-compile").length;
    if (["latex-dvi", "xelatex"].includes(COMPILER_CHOICE)) {
      record.coldOffline.converterWorkers = nativeWorkersInPhase("offline-compile").filter(worker => /\/dvi\?/.test(worker.url)).length;
      assert.equal(record.coldOffline.converterWorkers, 1, "one real cold-offline DVI conversion");
    }
    if (CASE === "long") {
      const n = await pageCount(page);
      record.coldOffline.pageCount = n;
      assert.ok(n >= 1, "offline page count >= 1");
      await goToPage(page, 1);
      assert.ok((await canvasNonwhite(page)) > 0, "offline long page 1 raster");
      await goToPage(page, n);
      assert.ok((await canvasNonwhite(page)) > 0, `offline long last page ${n} raster`);
      const lastText = await pageText(page);
      assert.match(lastText, /Edited offline/, "offline edited section rendered on last page");
      record.coldOffline.continuedSearch = await verifyLongSearch(page, project, "Kyiv 456.", offlineOutputHash, "offline-long-search");
      record.coldOffline.sectionRanges = await verifySectionRanges(page, project, offlineOutputHash, "offline-section-ranges", false);
    } else {
      assert.ok((await canvasNonwhite(page)) > 0, "offline canvas nonblank raster");
      const offlineText = await collectAllPageText(page);
      if (PACKAGE_CASE) {
        record.coldOffline.actualPageText = offlineText.slice(0, 4096);
        assert.deepEqual(await H.head(page, project), offlineSavedHead, 'offline package compile/render preserve every saved input');
        record.coldOffline.download = await downloadAcceptedPdf(page, project, 'offline-package-download', 'offline.pdf', offlineOutputHash);
        if(PACKAGE_COMPILER_ONLY){record.coldOffline.finalTexPass=assertPackageCompilerLog(await H.$(page,'compiler-log').textContent());record.coldOffline.realNativePhases=await observePackageCompilerPhases(page);}
      }
      if(FINAL_R1_CASE){
        record.coldOffline.actualPageText=offlineText.slice(0,8192);
        const offlineLog=await H.$(page,'compiler-log').textContent();
        record.coldOffline.finalTexPass=assertFinalR1Log(offlineLog);
        record.coldOffline.realNativePhases=await observeR1Phases(page);
        record.coldOffline.textEvidence=assertR1Text(offlineText,BIBER_CASE);
        record.coldOffline.literalSearch=await verifyLiteralR1Search(page,project,offlineOutputHash,BIBER_CASE);
        if(BIBER_CASE){
          record.coldOffline.changedBibliography.productionRevision=assertBiberEditRevision(record.online.realNativePhases,record.coldOffline.realNativePhases,
            await H.$(page,'compile-details').textContent(),offlineLog);
          assertCapturedR1Head(await H.head(page,project),Buffer.from(changedSource,'utf8'),true);
        }
        if(HARFBUZZ_CASE)assert.match(offlineLog,/This is LuaHBTeX, Version/);
      }
      assert.ok(offlineText.trim().length > 0, "offline page text nonblank");
      if (WORK_MAJOR_HEADING_CHECKS) assert.match(offlineText, /Modern CSharp pipeline/, "offline original heading fixture rendered");
      else assert.match(offlineText, /Edited offline/, "offline edited section rendered");
      if (FONT_CLOSURE || UNICODE_FONTS) {
        record.coldOffline.fontCoverage = assertFontClosure(await H.$(page, 'compiler-log').textContent(), offlineText);
      } else if (CASE === 't2a-bundled') {
        const norm = offlineText.replace(/\s+/g, ' ').trim();
        assert.equal(norm.split('Україна').length - 1, 2, 'offline bundled regular and bold text');
        assert.match(norm, /Єє Її Ґґ/);
        assert.match(norm, /ASCII I i 123\./);
        const log = await H.$(page, "compiler-log").textContent();
        assert.match(log, /COMPATIBILITY-UKRAINIAN-ID:[1-9][0-9]*/);
        assert.match(log, /COMPATIBILITY-ENGLISH-ID:0\b/);
        assert.doesNotMatch(log, /Missing character:|LaTeX Font Warning:|not loadable|File[^\n]*not found/);
        record.coldOffline.actualPageText = offlineText.slice(0, 4096);
        record.coldOffline.projectFontFiles = 0;
      }
      if (['packages', 'biblatex'].includes(FIXTURE_CASE)) {
        record.coldOffline.packageTextEvidence = sourcePdfNavigation.assertPackageText(offlineText, FIXTURE_CASE, COMPILER_CHOICE);
        const norm = offlineText.replace(/\s+/g, ' ').trim();
        assert.match(norm, /Hyperlink fixture/);
        assert.match(norm, /Listing marker/);
        assert.match(norm, /Package compatibility entry/);
        assert.match(norm, /Section 2 is resolved\./);
        assert.match(norm, /Portable Compiler Reference/);
        if (['xelatex', 'lualatex'].includes(COMPILER_CHOICE)) {
          assert.match(norm, /Привіт/); assert.match(norm, /Київ/);
        }
        const log = await H.$(page, "compiler-log").textContent();
        assert.doesNotMatch(log, /Missing character:|LaTeX Font Warning:|not loadable|File[^\n]*not found/);
        if (['xelatex', 'lualatex'].includes(COMPILER_CHOICE)) {
          assert.match(log, /COMPATIBILITY-UKRAINIAN-ID:[1-9][0-9]*/);
          assert.match(log, /COMPATIBILITY-ENGLISH-ID:0\b/);
          assert.doesNotMatch(log, /No hyphenation patterns were preloaded|Unknown language|Ukrainian hyphenation patterns are not loaded/i);
        }
        record.coldOffline.actualPageText = offlineText.slice(0, 4096);
      }
      if (CASE === "font-assets") {
        if (T2A_UNICODE_REPAIR) {
          record.coldOffline.actualPageText = offlineText.slice(0, 2048);
          record.coldOffline.actualPageTextCodepoints = Array.from(record.coldOffline.actualPageText)
            .map(character => "U+" + character.codePointAt(0).toString(16).toUpperCase().padStart(4, "0"));
          record.coldOffline.actualPageTextTruncated = offlineText.length > 2048;
        }
        record.coldOffline.fontTextEvidence = assertFontPageText(offlineText);
        if (T2A_UNICODE_REPAIR) record.coldOffline.latinTextEvidence = assertRepairLatinText(offlineText);
      }
    }

    if (MAIN_PATH !== "main.tex") {
      assert.deepEqual(await H.head(page, project), offlineSavedHead, "offline selected-root compile preserves saved files");
      record.coldOffline.selectedMain = MAIN_PATH;
      record.coldOffline.selectedDownload = await downloadAcceptedPdf(page, project, "selected-root-offline-download", "selected-offline.pdf", offlineOutputHash);
    }

    if (SOURCE_PDF_CHECKS) {
      phase = "offline-source-pdf-navigation";
      record.coldOffline.sourcePdfNavigation = await sourcePdfNavigation.verify(page, project, MAIN_PATH, BIBER_CASE ? "portable" : CASE === "portable" || PACKAGE_CASE ? CASE : CASE === "golden", phase);
    }
    if (CHECK_DOWNLOADS) {
      if (WORK_PAGE_NUMBER_CHECKS) {
        phase = 'offline-work-page-numbers';
        record.coldOffline.workPageNumbers = await workPageNumbers.verify(page, project, offlineOutputHash, () => workers, true);
      }
      if (WORK_MAJOR_HEADING_CHECKS) {
        phase = 'offline-work-major-heading';
        record.coldOffline.workMajorHeading = await workMajorHeading.verify(page, project, offlineOutputHash, () => workers, renderedReviewPreflight.renderer, true);
        assert.notEqual(record.coldOffline.workMajorHeading.authority.request,record.online.workMajorHeading.authority.request,
          "fresh accepted offline compile ticket for the same saved heading fixture");
      }
      if(RENDERED_REVIEW_CHECKS){
        phase='offline-rendered-review';
        record.coldOffline.renderedReview=await renderedReview.verify(page,{project,hash:offlineOutputHash,pages:await pageCount(page),
          workers:()=>workers.slice(renderedReviewWorkerStart),expected:renderedReviewPreflight,offline:true,
          previewReady:()=>waitPreviewAvailable(page,90000)});
      }
      assert.equal((await H.$(page, "pdf-hash").textContent()).trim(), offlineOutputHash);
      assert.deepEqual(await H.head(page, project), offlineSavedHead, "fresh offline compile/render keep saved project head and bytes");
      if (CASE === "font-assets") assertFontHead(offlineSavedHead, Buffer.from(changedSource, "utf8"));
      if (!PACKAGE_CASE) record.coldOffline.download = await downloadAcceptedPdf(page, project,
        CASE === "font-assets" ? "offline-font-download" : "offline-plain-download",
        CASE === "font-assets" ? "offline-font.pdf" : "offline.pdf", offlineOutputHash);
      if(FINAL_R1_CASE)record.coldOffline.downloadInspection=await inspectR1Download('offline.pdf',BIBER_CASE);
      record.coldOffline.freshCurrentVerified = true;
    }
    if (CHECK_DOWNLOADS && !WORK_MAJOR_HEADING_CHECKS) {
      // Deliberately finish stale: never restore/recompile to hide the changed source.
      // Establish an ordinary user-save boundary before testing download; autosave may run during preview refresh.
      phase = CASE === "font-assets" ? "controlled-stale-font-source" : "controlled-stale-plain-source";
      const workingOnly = T2A_UNICODE_REPAIR
        ? changedSource.replace(token, "{\\fontencoding{OT1}\\selectfont\n\\noindent Working edit not compiled. \\par}\n" + token)
        : changedSource.replace(token, "\\noindent Working edit not compiled. \\par\n" + token);
      await H.$(page, "editor").fill(workingOnly);
      await page.waitForFunction(() => document.querySelector('[data-testid=compile-panel]')?.dataset.isCurrent === 'false', null, { timeout: 30000 });
      await page.waitForFunction(() => /Last successful PDF.*out of date/.test(document.querySelector('[data-testid=compiled-pdf-status]')?.textContent || ''), null, { timeout: 30000 });
      if(RENDERED_REVIEW_CHECKS)record.coldOffline.renderedReview.sourceStale=await renderedReview.sourceStale(page,
        {hash:offlineOutputHash,workers:()=>workers.slice(renderedReviewWorkerStart)});
      // Ordinary autosave may commit while the stale preview refreshes. Save
      // through the real UI first, then prove download makes no further save,
      // source mutation, native work or promotion of the stale compiled PDF.
      phase = "explicit-stale-source-save";
      await H.save(page);
      const staleSourceHead = await H.head(page, project);
      const expectedStaleFiles = offlineSavedHead.files.map(file => file.path === MAIN_PATH
        ? { ...file, bytes: Array.from(Buffer.from(workingOnly, "utf8")) } : file);
      assert.deepEqual(staleSourceHead.files, expectedStaleFiles, "explicit stale Save changes only the deliberate working edit");
      if (CASE === "font-assets") assertFontHead(staleSourceHead, Buffer.from(workingOnly, "utf8"));
      const sourceHash = sha256(Buffer.from(staleSourceHead.files.find(file => file.path === MAIN_PATH).bytes));
      assert.equal(sourceHash, sha256(Buffer.from(workingOnly, "utf8")), "explicit stale Save persists the exact changed source");
      assert.notEqual(sourceHash, record.coldOffline.editedSourceSha256, "saved stale source differs from the compiled source");
      record.coldOffline.explicitStaleSave = { action: "ordinary user Save before export", headId: staleSourceHead.id, sourceSha256: sourceHash };
      assert.equal(await H.$(page, "compile-panel").getAttribute("data-output-hash"), offlineOutputHash, "explicit source Save does not compile or replace the previous PDF");
      assert.equal(await H.$(page, "compile-panel").getAttribute("data-is-current"), "false", "source Save keeps the previous PDF stale");
      assert.match(await H.$(page, "compiled-pdf-status").textContent(), /Last successful PDF.*out of date/);
      // Stale-label propagation schedules a real preview refresh. Let it settle
      // before the download phase so queued rendering cannot masquerade as export work.
      await page.waitForFunction(() => /CallerSaysHistorical/.test(document.querySelector('[data-testid=pdf-provenance]')?.textContent || ''), null, { timeout: 30000 });
      await waitPreviewAvailable(page, 90000);
      assert.equal(await H.$(page, "pdf-pane").getAttribute("data-document"), offlineOutputHash);
      record.coldOffline.staleDownload = await downloadAcceptedPdf(page, project,
        CASE === "font-assets" ? "stale-font-download" : "stale-plain-download",
        CASE === "font-assets" ? "stale-font.pdf" : "stale.pdf", offlineOutputHash);
      assert.equal(await H.$(page, "compile-panel").getAttribute("data-is-current"), "false");
      assert.match(await H.$(page, "compiled-pdf-status").textContent(), /Last successful PDF.*out of date/);
      assert.equal(await H.$(page, "editor").inputValue(), workingOnly);
      const finalHead = await H.head(page, project);
      assert.deepEqual(finalHead, staleSourceHead, "stale export preserves the explicit user-save baseline and asset bytes");
      if (CASE === "font-assets") assertFontHead(finalHead, Buffer.from(workingOnly, "utf8"));
      record.coldOffline.finalState = "changed source explicitly saved; last successful offline PDF retained stale";
      record.coldOffline.finalPdfCurrent = false;
    }

    violations.push(...(await page.evaluate(() => window.__authorCsp || [])));
    assert.deepEqual(externalSuccesses, [], "no external successes");
    assert.deepEqual(errors, [], "no page errors");
    assert.deepEqual(violations, [], "no CSP violations");

    record.result = "PASS";
    if (RESULT_DIR) await page.screenshot({ path: path.join(RESULT_DIR, `local-authoring-${CASE}-offline.png`), fullPage: true });
    process.stdout.write(JSON.stringify(record) + "\n");
  } catch (error) {
    record.failure = {
      phase,
      message: String(error.message).slice(0, 1024),
      stack: error.stack ? error.stack.slice(0, 2048) : null,
    };
    // Capture sanitized actual UI status at failure (no source/keys/paths).
    // Bounded compiler-log/details/diagnostics text is necessary to diagnose a
    // real compile failure (e.g. font/package gaps); this is a uniquely owned
    // synthetic browser profile with no user/cloud credentials. Read-only: no
    // init/worker/provision calls; only the bridge's EXISTING bounded
    // observation/trace via the cached module already imported by C#.
    try {
      if (context) {
        // launchPersistentContext starts a blank about:blank tab; launch()
        // creates the actual app page. Select the existing app page by EXACT
        // origin — do not blindly choose the first/last tab or create a page.
        const p = context.pages().find((candidate) => {
          try { return new URL(candidate.url()).origin === base; } catch { return false; }
        });
        if (p) {
          const diag = await p.evaluate(async () => {
            const text = (sel, max) => {
              const el = document.querySelector(sel);
              if (!el) return null;
              const t = el.textContent || "";
              return t.length > max ? t.slice(0, max) + "…[truncated]" : t;
            };
            const status = {
              pageLabel: document.querySelector('[data-testid=page-label]')?.textContent || null,
              canvasStatus: document.querySelector('[data-testid=canvas-status]')?.textContent || null,
              goPageValue: document.querySelector('input[aria-label="Go to page"]')?.value || null,
              searchStatus: document.querySelector('[data-testid=search-status]')?.dataset.searchStatus || null,
              compile: document.querySelector("[data-testid=compile-status]")?.textContent || null,
              compiledPdf: document.querySelector("[data-testid=compiled-pdf-status]")?.textContent || null,
              preview: document.querySelector("[data-testid=preview-status]")?.dataset.previewStatus || null,
              previewDetails: text("[data-testid=pdf-details]", 4096),
              runtime: document.querySelector("[data-testid=runtime-status]")?.dataset.state || null,
              pdfRuntime: document.querySelector("[data-testid=pdf-runtime-status]")?.dataset.state || null,
              readiness: document.querySelector("[data-testid=compiler-readiness]")?.textContent || null,
              sourceMap: document.querySelector("[data-testid=source-map-status]")?.textContent || null,
              outputHash: document.querySelector("[data-testid=compile-panel]")?.getAttribute("data-output-hash") || null,
              isCurrent: document.querySelector("[data-testid=compile-panel]")?.getAttribute("data-is-current") || null,
              pdfDownloadStatus: text('[data-testid=pdf-download-status]', 1024),
            };
            const compilerLog = text("[data-testid=compiler-log]", 16384);
            const compileDetails = text("[data-testid=compile-details]", 4096);
            const compilerDiagnostics = text("[data-testid=compiler-diagnostics]", 4096);
            // Bridge's EXISTING bounded observation/trace via the SAME canonical
            // module import C# already used; the cached import is present and
            // its top-level creates no worker. Read observations() ONLY: never
            // provision/execute/dispose/init/construct a Worker or inject a
            // global substitute. The existing 64-entry bound applies; no
            // keys/source/other files are present in the trace.
            let bridgeObservations = null;
            try {
              const bridge = await import("./js/compile-port-bridge.js");
              if (bridge && typeof bridge.observations === "function") {
                bridgeObservations = bridge.observations();
              }
            } catch {}
            return { status, compilerLog, compileDetails, compilerDiagnostics, bridgeObservations };
          });
          record.failure.uiStatus = diag.status;
          record.failure.workerEntries = workers.map(worker => ({ phase: worker.phase, url: worker.url }));
          if (diag.compilerLog) record.failure.compilerLog = diag.compilerLog;
          if (diag.compileDetails) record.failure.compileDetails = diag.compileDetails;
          if (diag.compilerDiagnostics) record.failure.compilerDiagnostics = diag.compilerDiagnostics;
          if (diag.bridgeObservations) record.failure.bridgeObservations = diag.bridgeObservations;
          if (RESULT_DIR && diag.status.outputHash && await H.$(p, "download-pdf").isEnabled()) {
            const pending = p.waitForEvent("download", { timeout: 5000 });
            await H.$(p, "download-pdf").click();
            const download = await pending;
            const filename = path.join(RESULT_DIR, "failure-compiled.pdf");
            await download.saveAs(filename);
            assert.equal(sha256(fs.readFileSync(filename)), diag.status.outputHash);
            record.failure.compiledPdfSaved = true;
          }
        }
      }
    } catch {}
    process.exitCode = 1;
  } finally {
    clearTimeout(deadline);
    try { if (context) await context.close(); } catch {}
    try {
      if (server.listening) await new Promise((r) => server.close(r));
    } catch {}
    if (profile) fs.rmSync(profile, { recursive: true, force: true });
    save();
    if (record.failure) process.stderr.write(JSON.stringify({ result: record.result, failure: record.failure }) + "\n");
  }
})();
