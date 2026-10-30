#!/usr/bin/env node
'use strict';
// R4 B1 tooling only. Inventory and supplied-observation predicates, NEVER native
// execution. No subprocess/browser/network/package lookup or dynamic scheduler.
const fs = require('node:fs');
const path = require('node:path');
const { createHash } = require('node:crypto');
const { TextDecoder } = require('node:util');
const ROOT = path.resolve(__dirname, '..');
const FIXTURES = path.join(ROOT, 'tests/ReleaseSecurityFixtures');
const MAX_METADATA = 131072;
const SOURCE_FILES = [
    'src/Client/wwwroot/js/compile-port-bridge.js', 'src/Client/wwwroot/js/pdf-port-bridge.js',
    'src/Client/wwwroot/js/synctex-query-bridge.js', 'src/Client/wwwroot/js/restricted-worker-admission.js',
    'src/Client/wwwroot/js/compile-port-worker.js', 'src/Client/wwwroot/js/unicode-tex-port-worker.js',
    'src/Client/wwwroot/js/pdf-port-worker.js', 'src/Client/wwwroot/js/synctex-query-worker.js',
    'src/Client/Compilation/Native/NativeCompilerSuiteContract.cs',
    'src/Client/Compilation/Native/NativePortContracts.cs', 'src/Client/Pdf/PdfContracts.cs',
    'src/Client/Compilation/CompilationContracts.cs', 'src/Client/Compilation/CompileRequestContracts.cs',
    'src/Client/Workspace/Domain/Ids.cs',
    'src/Client/Compilation/Native/NativeSyncTeXContract.cs',
    'src/Client/Compilation/SyncTeX/SyncTeXContracts.cs',
];
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const isHash = value => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
class Refusal extends Error { constructor(code, message) { super(message); this.code = code; } }
function requireInput(value, message, code = 2) { if (!value) throw new Refusal(code, message); }
function canonical(value) {
    const order = item => Array.isArray(item) ? item.map(order) : item && typeof item === 'object'
        ? Object.fromEntries(Object.keys(item).sort().map(key => [key, order(item[key])])) : item;
    return JSON.stringify(order(value)) + '\n';
}
function readMetadata(file, canonicalRequired = false) {
    const stat = fs.lstatSync(file);
    requireInput(stat.isFile() && !stat.isSymbolicLink() && stat.size <= MAX_METADATA, 'metadata size/type invalid');
    const bytes = fs.readFileSync(file);
    requireInput(bytes.length <= MAX_METADATA, 'metadata size invalid');
    let text, result;
    try { text = new TextDecoder('utf-8', { fatal: true }).decode(bytes); result = JSON.parse(text); }
    catch { throw new Refusal(2, 'metadata is not valid UTF8 JSON'); }
    if (canonicalRequired) requireInput(text === canonical(result), 'observation must use compact ordinal JSON and LF (no duplicate fields)');
    return { value: result, sha256: sha(bytes) };
}
function fileFact(relative, base = ROOT) {
    const file = path.join(base, relative), stat = fs.lstatSync(file);
    requireInput(stat.isFile() && !stat.isSymbolicLink(), 'regular source file required');
    requireInput(stat.size <= 65536, 'source fixture/tooling input bound');
    const bytes = fs.readFileSync(file);
    return { path: relative, length: bytes.length, sha256: sha(bytes) };
}
function preflight() {
    requireInput(!fs.lstatSync(FIXTURES).isSymbolicLink(), 'fixture directory symlink refused');
    const metadata = readMetadata(path.join(FIXTURES, 'manifest.json')), m = metadata.value;
    requireInput(m && m.schemaVersion === 1 && m.synthetic === true && m.execution === 'preflight-only', 'unsupported fixture manifest');
    requireInput(Array.isArray(m.files) && m.files.length > 0 && m.files.length <= 32
        && Array.isArray(m.cases) && m.cases.length > 0 && m.cases.length <= 32, 'bounded inventory required');
    const seen = new Set();
    for (const fact of m.files) {
        requireInput(fact && typeof fact.path === 'string' && /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/.test(fact.path)
            && /\.(tex|lua|md)$/.test(fact.path) && !seen.has(fact.path.toLowerCase()), 'fixture filename/alias invalid');
        seen.add(fact.path.toLowerCase());
        const actual = fileFact(fact.path, FIXTURES);
        requireInput(fact.length === actual.length && fact.sha256 === actual.sha256, 'fixture source hash/length mismatch', 4);
    }
    const declared = new Set(m.files.map(f => f.path));
    const actualNames = fs.readdirSync(FIXTURES).filter(name => name !== 'manifest.json');
    requireInput(actualNames.length === declared.size && actualNames.every(name => declared.has(name)), 'missing/extra fixture source', 3);
    const ids = new Set();
    for (const fixture of m.cases) {
        requireInput(typeof fixture.id === 'string' && /^[a-z0-9-]{1,64}$/.test(fixture.id) && !ids.has(fixture.id), 'invalid/duplicate case ID');
        ids.add(fixture.id);
        requireInput(Array.isArray(fixture.files) && fixture.files.length > 0 && fixture.files.every(name => declared.has(name))
            && fixture.files.includes(fixture.entry), 'case source association missing', 3);
        requireInput(['cpu', 'admission', 'quota', 'display'].includes(fixture.mode), 'unknown case mode');
        requireInput(Array.isArray(fixture.engineIds) && fixture.engineIds.length > 0
            && fixture.engineIds.every(id => ['pdftex', 'latex-dvi', 'xelatex', 'lualatex'].includes(id)), 'invalid engine eligibility');
        const b = fixture.limits;
        requireInput(b && Number.isSafeInteger(b.outerWallMs) && b.outerWallMs > 0 && b.outerWallMs <= 30000
            && Number.isSafeInteger(b.heapBytes) && b.heapBytes > 0 && b.heapBytes <= 2147483648
            && Number.isSafeInteger(b.outputBytes) && b.outputBytes > 0 && b.outputBytes <= 8388608
            && Number.isSafeInteger(b.logBytes) && b.logBytes > 0 && b.logBytes <= 262144
            && b.cancelRetireMs === 2000, 'case limits invalid');
    }
    return { manifest: m, fixtureManifestSha256: metadata.sha256, sourceFacts: SOURCE_FILES.map(name => fileFact(name)) };
}
function checkObservation(fixture, record, expected, options = {}) {
    // Pure predicates over supplied evidence. Root must independently review the
    // trace proving the statements; this tool cannot authenticate execution.
    const errors = [], check = (condition, message) => { if (!condition) errors.push(message); };
    check(record?.schemaVersion === 1, 'observation schema');
    const synthetic = record?.evidenceKind === 'synthetic-oracle-vector';
    check(record?.evidenceKind === 'root-browser-capture' || options.allowSynthetic === true && synthetic,
        'real root browser evidence required; synthetic vectors are not runtime captures');
    check(record?.fixtureId === fixture.id && record?.fixtureManifestSha256 === expected.fixtureManifestSha256,
        'fixture/corpus identity mismatch');
    const identity = record?.identity;
    for (const field of ['sourceTreeSha256', 'runtimeIdentitySha256', 'publicationIdentitySha256'])
        check(isHash(expected[field]) && identity?.[field] === expected[field], 'expected ' + field + ' mismatch');
    check(fixture.engineIds.includes(identity?.engineId), 'wrong engine for fixture');
    check(Number.isSafeInteger(identity?.profile) && identity.profile >= 0 && identity.profile === expected.profile,
        'profile must equal independently supplied identity (never >= admission)');
    const supervisor = record?.supervisor;
    check(supervisor?.cpuQuota === 2 && Number.isSafeInteger(supervisor?.memoryBytes)
        && supervisor.memoryBytes <= 2147483648 && supervisor.memoryBytes > 0
        && Number.isSafeInteger(supervisor?.deadlineMs) && supervisor.deadlineMs <= fixture.limits.outerWallMs
        && supervisor.deadlineMs > 0, 'bounded root supervisor required');
    const o = record?.observed || {}, b = fixture.limits;
    const bounded = (value, maximum) => Number.isSafeInteger(value) && value >= 0 && value <= maximum;
    check(bounded(o.elapsedMs, b.outerWallMs) && bounded(o.peakHeapBytes, b.heapBytes)
        && bounded(o.outputBytes, b.outputBytes) && bounded(o.logBytes, b.logBytes), 'measured resource bounds');
    check(fixture.expected.statuses.includes(o.status), 'unexpected terminal status');
    check(o.outerSupervisorKilled === false, 'outer kill is failure, not product cancellation');
    check(o.outputPromoted === false || fixture.mode === 'display' && o.outputPromoted === true,
        'partial output cannot be promoted; display promotion observation must be explicit');
    check(o.sourceEditableAfter === true && o.benignRecoveryCompleted === true, 'editor and benign recovery required');
    check(isHash(o.savedProjectBeforeSha256) && o.savedProjectBeforeSha256 === o.savedProjectAfterSha256,
        'saved project bytes changed');
    check(o.networkRequests === 0 && o.processLaunches === 0 && o.hostCanaryRead === false,
        'independent no-side-effect observations required');
    if (fixture.mode === 'admission')
        check(o.workerCreated === false && o.nativeEntered === false, 'admission must refuse before Worker/native');
    else {
        check(o.nativeEntered === true && o.nativeActivityProof === 'native-workload-trace',
            'before-call marker is not actual native activity proof');
        check(o.workerDestroyed === true && bounded(o.workerDestroyLatencyMs, b.cancelRetireMs),
            'physical retirement observation required');
    }
    if (fixture.mode === 'quota')
        check(o.quotaLatched === true && o.outputGettersRevoked === true, 'caught quota must remain latched; truncation is not a latch');
    if (fixture.mode === 'display')
        check(o.displayExecuted === false && o.uriNavigations === 0, 'display/URI data must remain passive');
    return { ok: errors.length === 0, errors, scope: synthetic ? 'synthetic-oracle-vector' : 'supplied-observation-only',
        execution: 'not-run-by-this-tool' };
}
function cli(args = process.argv.slice(2)) {
    if (args.includes('--help')) {
        console.log('R4 B1 fixture preflight (default): node scripts/test-release-isolation.cjs [--preflight]\n'
            + 'Receipt oracle: --check-observation <canonical.json> --source-sha256 <sha> --runtime-sha256 <sha> '
            + '--publication-sha256 <sha> --profile <integer>\n'
            + '--run-hostile is refused: root-controlled accepted runtime/publication/supervisor capture is not implemented here.');
        return 0;
    }
    if (args.includes('--run-hostile')) throw new Refusal(3,
        'root-controlled hostile runtime stage unavailable; requires explicit accepted runtime/publication and bounded supervisor; no execution performed');
    const inventory = preflight();
    if (!args.length || args.length === 1 && args[0] === '--preflight') {
        console.log(JSON.stringify({ schemaVersion: 1, id: 'R4-RES-01/B1-fixture-preflight', status: 'pass',
            command: 'node scripts/test-release-isolation.cjs --preflight', receipt: 'stdout',
            fixtureManifestSha256: inventory.fixtureManifestSha256, fixtureFiles: inventory.manifest.files,
            sourceFacts: inventory.sourceFacts, cases: inventory.manifest.cases.map(f => f.id),
            execution: 'not-run', scope: 'fixture-integrity-only',
            unproved: ['Real WASM/hostile execution', 'Browser CSP/network sinks', 'Physical in-call cancellation',
                'UI/storage availability and passive PDF action handling', 'Final profile7/source/runtime/publication identity'] }));
        return 0;
    }
    requireInput(args.length === 10 && args[0] === '--check-observation', 'unknown arguments (use --help)');
    const flags = new Map();
    for (let i = 2; i < args.length; i += 2) { requireInput(!flags.has(args[i]), 'duplicate option'); flags.set(args[i], args[i + 1]); }
    requireInput([...flags.keys()].sort().join(',') === '--profile,--publication-sha256,--runtime-sha256,--source-sha256', 'exact expected identities required');
    const record = readMetadata(args[1], true).value;
    const fixture = inventory.manifest.cases.find(f => f.id === record?.fixtureId);
    requireInput(fixture, 'unknown fixture ID');
    const expected = { fixtureManifestSha256: inventory.fixtureManifestSha256,
        sourceTreeSha256: flags.get('--source-sha256'), runtimeIdentitySha256: flags.get('--runtime-sha256'),
        publicationIdentitySha256: flags.get('--publication-sha256'), profile: Number(flags.get('--profile')) };
    requireInput(/^\d+$/.test(flags.get('--profile')), 'profile must be an explicit integer');
    const result = checkObservation(fixture, record, expected);
    console.log(JSON.stringify({ schemaVersion: 1, id: fixture.acceptanceIds[0] + '/B1-observation-oracle',
        status: result.ok ? 'pass' : 'fail', command: 'node scripts/test-release-isolation.cjs --check-observation <supplied receipt and identities>',
        receipt: 'stdout', ...result, claims: 'Predicate evaluation only; root must authenticate/review actual traces. This is not a new runtime run.' }));
    return result.ok ? 0 : 2;
}
if (require.main === module) {
    try { process.exitCode = cli(); }
    catch (error) {
        console.error('R4 isolation preflight: ' + (error instanceof Refusal ? error.message : 'invalid/unavailable declared input'));
        process.exitCode = error instanceof Refusal ? error.code : 2;
    }
}
module.exports = { preflight, checkObservation, canonical, cli, SOURCE_FILES };
