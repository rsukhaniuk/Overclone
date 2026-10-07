const assert = require("node:assert/strict");
const { chromium } = require("playwright");

// Real-Chromium analysis-worker transport/adapter coverage (M2a-2b-1, review round).
// The happy path and every RECOVERY step import the REAL rust-bridge ES
// module and run the REAL wasm-bindgen WASM through the canonical v1 envelope.
// FAULT cases use a test-local Worker replacement via Playwright route
// interception (deterministic, NOT a product debug API); each fault case
// is followed by a SAME-page recovery that removes the synthetic route and
// runs actual Rust WASM again, asserting a correct diagnostic AND that the
// worker epoch changed (the reset bumped it). Synthetic cases are labeled.
//
// Same-origin bundled WASM only — no runtime CDN. Playwright/npm installation
// in the container is tooling, not a PWA dependency.

const APP_URL = process.env.APP_URL || "http://app:8080";
const ANALYSIS_DEADLINE_MS = 5000;
const WG_EXACT = "9007199254740993"; // > 2^53; must survive byte-for-byte

// A fake worker that responds to legacy `ready` and canonical `initialize`
// quickly, then is deliberately nonresponsive for `analyzeSource` (so the
// whole-job 5000ms deadline or a main-thread cancel settles it).
const NONRESPONSIVE_WORKER = `
self.onmessage = ({ data }) => {
  if (data && typeof data.protocolVersion === "number") {
    if (data.operation === "initialize") {
      self.postMessage({ canonical: true, requestId: data.requestId, responseJson:
        JSON.stringify({ protocolVersion:1, requestId: data.requestId, status: "completed",
          payload: { engineVersion: "fake", operations: ["analyzeSource"], limits: {} } }) });
      return;
    }
    if (data.operation === "analyzeSource") return; // never reply
  }
  if (data.type === "ready") self.postMessage({ id: data.id, result: true });
};
`;

// A fake worker that counts initialize messages via a same-origin BroadcastChannel
// (NOT a network request, so not subject to Playwright route interception limits) and
// holds `ready` until released, then is nonresponsive for analyzeSource.
const INIT_COUNTING_WORKER = (releaseReady) => `
const bc = new BroadcastChannel("oopInitCount");
let readyReleased = ${releaseReady ? "true" : "false"};
let initCount = 0;
self.addEventListener("message", ({ data }) => {
  if (data && typeof data.protocolVersion === "number") {
    if (data.operation === "initialize") {
      initCount++;
      bc.postMessage(initCount);
      self.postMessage({ canonical: true, requestId: data.requestId, responseJson:
        JSON.stringify({ protocolVersion:1, requestId: data.requestId, status: "completed",
          payload: { engineVersion: "fake", operations: ["analyzeSource"], limits: {} } }) });
      return;
    }
    if (data.operation === "analyzeSource") return; // never reply
  }
  if (data.type === "ready") {
    if (!readyReleased) { self.__oopReadyHeld = true; return; }
    self.postMessage({ id: data.id, result: true });
  }
});
self.__oopReleaseReady = () => { readyReleased = true; if (self.__oopReadyHeld) self.postMessage({ id: 1, result: true }); };
`;

// A fake worker that crashes (uncaught SYNCHRONOUS error) on the first
// analyzeSource — a synchronous throw in a message handler fires
// worker.onerror on the parent.
const CRASH_WORKER = `
self.onmessage = ({ data }) => {
  if (data && typeof data.protocolVersion === "number") {
    if (data.operation === "initialize") {
      self.postMessage({ canonical: true, requestId: data.requestId, responseJson:
        JSON.stringify({ protocolVersion:1, requestId: data.requestId, status: "completed",
          payload: { engineVersion: "fake", operations: ["analyzeSource"], limits: {} } }) });
      return;
    }
    if (data.operation === "analyzeSource") throw new Error("deliberate worker crash");
  }
  if (data.type === "ready") self.postMessage({ id: data.id, result: true });
};
`;

// SYNTHETIC lifecycle scenarios (test-local fake Worker; NOT real WASM): a single
// self-contained driver function sets up a fake Worker (records postMessage,
// exposes onmessage/onerror/onmessageerror for the scenario to dispatch) and
// runs the selected scenario. The bridge installs its REAL handlers on the
// fake, so the routing/lifecycle logic under test is production code.
async function syntheticDriver({ scenarioId, deadlineMs }) {
  class FakeWorker {
    constructor() {
      this.terminated = false;
      this.messages = [];
      this.postMessageCount = 0;
    }
    postMessage(m, _t) {
      this.messages.push(m);
      this.postMessageCount++;
    }
    terminate() {
      this.terminated = true;
    }
  }
  const fakes = [];
  window.Worker = class extends FakeWorker {
    constructor() {
      super();
      fakes.push(this);
    }
  };
  const mod = await import("/js/rust-bridge.js");
  const enc = new TextEncoder();
  const buildMeta = (rid, n) =>
    JSON.stringify({
      protocolVersion: 1,
      requestId: rid,
      operation: "analyzeSource",
      payload: {
        snapshotId: "snap-1",
        projectId: "proj-1",
        sessionId: "sess-1",
        workingGeneration: 5,
        baseHeadRevisionId: null,
        profile: { id: "engine-structural", version: "0" },
        limits: {},
        files: [
          {
            fileId: "f1",
            relativePath: "main.tex",
            mediaKind: "text",
            hasBom: false,
            sha256: "x",
            byteOffset: 0,
            byteLength: n,
          },
        ],
      },
    });
  const initCompletedJson = (rid) =>
    JSON.stringify({
      protocolVersion: 1,
      requestId: rid,
      status: "completed",
      payload: {
        engineVersion: "fake",
        operations: ["analyzeSource"],
        limits: {},
      },
    });
  const analyzeCompletedJson = (rid) =>
    JSON.stringify({
      protocolVersion: 1,
      requestId: rid,
      status: "completed",
      payload: {
        snapshotId: "snap-1",
        profile: { id: "engine-structural", version: "0" },
        facts: {
          files: [
            {
              fileId: "f1",
              byteLength: 0,
              hasBom: false,
              newlineKind: "lf",
              truncated: false,
              braceDepthExceeded: false,
              tokenCount: 0,
              engineOutcome: "pass",
              tokens: [],
            },
          ],
        },
        diagnostics: [],
        ruleResults: [
          {
            ruleId: "ENGINE-STRUCT-001",
            origin: "engine",
            outcome: "pass",
            perFile: [{ fileId: "f1", outcome: "pass", diagnosticIds: [] }],
            limitations: [],
          },
        ],
        aggregateOutcome: "pass",
        coverage: {
          factKinds: [],
          unsupported: [],
          truncatedFiles: [],
          diagnosticsTruncated: false,
          resultTruncated: false,
        },
        unavailableCapabilities: ["pdfFacts", "pdfChecks"],
      },
    });
  const yieldMicro = () => new Promise((r) => setTimeout(r, 0));
  const waitFor = (cond) =>
    new Promise((resolve, reject) => {
      const t = setInterval(() => {
        try {
          if (cond()) {
            clearInterval(t);
            resolve();
          }
        } catch (e) {
          clearInterval(t);
          reject(e);
        }
      }, 0);
      setTimeout(() => {
        clearInterval(t);
        reject(new Error("waitFor timeout"));
      }, 5000);
    });
  const statusOf = (s) => {
    try {
      return JSON.parse(s).status;
    } catch {
      return "<unparseable>";
    }
  };
  const codeOf = (s) => {
    try {
      return JSON.parse(s).error?.code;
    } catch {
      return undefined;
    }
  };
  const driveReadyInit = (f) => {
    f.onmessage({ data: { id: f.messages[0].id, result: true } });
    return yieldMicro().then(() => {
      const im = f.messages.find((m) => m.operation === "initialize");
      f.onmessage({
        data: {
          canonical: true,
          requestId: im.requestId,
          responseJson: initCompletedJson(im.requestId),
        },
      });
      return yieldMicro();
    });
  };
  // Install unhandledrejection capture BEFORE any cancellation/failure scenario
  // starts, so a discarded rejecting promise chain is observed regardless of
  // which scenario runs (deterministic; the event is delivered at end-of-turn).
  const unhandled = [];
  self.addEventListener("unhandledrejection", () => unhandled.push(true));
  switch (scenarioId) {
    case "initConcurrency": {
      const p1 = enc.encode("a");
      const p2 = enc.encode("b");
      const j1 = mod.analyzeSource(buildMeta("ic1", p1.length), p1);
      const j2 = mod.analyzeSource(buildMeta("ic2", p2.length), p2);
      const f0 = fakes[0];
      f0.onmessage({ data: { id: f0.messages[0].id, result: true } });
      await yieldMicro();
      const im = f0.messages.find((m) => m.operation === "initialize");
      f0.onmessage({
        data: {
          canonical: true,
          requestId: im.requestId,
          responseJson: initCompletedJson(im.requestId),
        },
      });
      await waitFor(
        () =>
          f0.messages.filter((m) => m.operation === "analyzeSource").length >=
          2,
      );
      const initCount = f0.messages.filter(
        (m) => m.operation === "initialize",
      ).length;
      const analyzeCount = f0.messages.filter(
        (m) => m.operation === "analyzeSource",
      ).length;
      mod.cancelSource("ic1");
      mod.cancelSource("ic2");
      await j1;
      await j2;
      return { initCount, analyzeCount };
    }
    case "completedEpochRetained": {
      const packed = enc.encode("s1");
      const job = mod.analyzeSource(buildMeta("s1", packed.length), packed);
      const f0 = fakes[0];
      await driveReadyInit(f0);
      const am = f0.messages.find((m) => m.operation === "analyzeSource");
      f0.onmessage({
        data: {
          canonical: true,
          requestId: am.requestId,
          responseJson: analyzeCompletedJson(am.requestId),
        },
      });
      mod.dispose();
      const r = await job;
      return {
        status: statusOf(r.responseJson),
        workerEpoch: r.workerEpoch,
        currentEpoch: mod.getWorkerEpoch(),
      };
    }
    case "staleOldWorkerReply": {
      const p1 = enc.encode("s2a");
      const j1 = mod.analyzeSource(buildMeta("s2a", p1.length), p1);
      const f0 = fakes[0];
      await driveReadyInit(f0);
      const am = f0.messages.find((m) => m.operation === "analyzeSource");
      f0.onmessage({
        data: {
          canonical: true,
          requestId: am.requestId,
          responseJson: analyzeCompletedJson(am.requestId),
        },
      });
      const r1 = await j1;
      f0.onerror();
      const p2 = enc.encode("s2b");
      const j2 = mod.analyzeSource(buildMeta("s2b", p2.length), p2);
      const f1 = fakes[1];
      await driveReadyInit(f1);
      const am1 = f1.messages.find((m) => m.operation === "analyzeSource");
      f0.onmessage({
        data: {
          canonical: true,
          requestId: am1.requestId,
          responseJson: analyzeCompletedJson(am1.requestId),
        },
      });
      f1.onmessage({
        data: {
          canonical: true,
          requestId: am1.requestId,
          responseJson: analyzeCompletedJson(am1.requestId),
        },
      });
      const r2 = await j2;
      return {
        r1Epoch: r1.workerEpoch,
        r2Status: statusOf(r2.responseJson),
        r2Epoch: r2.workerEpoch,
      };
    }
    case "postMessageThrow": {
      const p1 = enc.encode("s3a");
      const j1 = mod.analyzeSource(buildMeta("s3a", p1.length), p1);
      const f0 = fakes[0];
      f0.postMessage = (m, _t) => {
        f0.messages.push(m);
        if (m && m.operation === "analyzeSource")
          throw new TypeError("detached");
      };
      await driveReadyInit(f0);
      const r1 = await j1;
      const p2 = enc.encode("s3b");
      const j2 = mod.analyzeSource(buildMeta("s3b", p2.length), p2);
      const f1 = fakes[1];
      await driveReadyInit(f1);
      const am1 = f1.messages.find((m) => m.operation === "analyzeSource");
      f1.onmessage({
        data: {
          canonical: true,
          requestId: am1.requestId,
          responseJson: analyzeCompletedJson(am1.requestId),
        },
      });
      const r2 = await j2;
      await new Promise((r) => setTimeout(r, deadlineMs + 500));
      return {
        r1Status: statusOf(r1.responseJson),
        r1Code: codeOf(r1.responseJson),
        r2Status: statusOf(r2.responseJson),
        currentEpoch: mod.getWorkerEpoch(),
      };
    }
    case "readyFailureRetry": {
      const p1 = enc.encode("s4a");
      const j1 = mod.analyzeSource(buildMeta("s4a", p1.length), p1);
      const f0 = fakes[0];
      f0.onmessage({ data: { id: f0.messages[0].id, error: "ready boom" } });
      const r1 = await j1;
      const p2 = enc.encode("s4b");
      const j2 = mod.analyzeSource(buildMeta("s4b", p2.length), p2);
      const f1 = fakes[1];
      await driveReadyInit(f1);
      const am1 = f1.messages.find((m) => m.operation === "analyzeSource");
      f1.onmessage({
        data: {
          canonical: true,
          requestId: am1.requestId,
          responseJson: analyzeCompletedJson(am1.requestId),
        },
      });
      const r2 = await j2;
      return {
        r1Status: statusOf(r1.responseJson),
        r1Code: codeOf(r1.responseJson),
        r2Status: statusOf(r2.responseJson),
      };
    }
    case "legacyStaleReply": {
      const cw1p = mod.countWords("hello");
      const f0 = fakes[0];
      // legacy ready (countWords uses the legacy ready handshake, NOT canonical init)
      f0.onmessage({ data: { id: f0.messages[0].id, result: true } });
      await yieldMicro();
      const cwMsg = f0.messages.find((m) => m.type === "countWords");
      f0.onmessage({ data: { id: cwMsg.id, result: 5 } });
      const cw1 = await cw1p;
      f0.onerror();
      const cw2p = mod.countWords("world"); // new worker f1 (epoch 1)
      const f1 = fakes[1];
      const newReadyId = f1.messages[0].id;
      // STALE legacy reply from f0 (epoch 0 closure) with the NEW ready id -> dropped.
      f0.onmessage({ data: { id: newReadyId, result: "stale" } });
      // fresh legacy reply on f1 resolves the new ready.
      f1.onmessage({ data: { id: newReadyId, result: true } });
      await yieldMicro();
      const cwMsg1 = f1.messages.find((m) => m.type === "countWords");
      f1.onmessage({ data: { id: cwMsg1.id, result: 7 } });
      const cw2 = await cw2p;
      return { cw1, cw2, staleDropped: cw2 !== "stale" };
    }
    case "initFailedStatus": {
      const packed = enc.encode("x");
      const job = mod.analyzeSource(buildMeta("ifs", packed.length), packed);
      const f0 = fakes[0];
      f0.onmessage({ data: { id: f0.messages[0].id, result: true } });
      await yieldMicro();
      const im = f0.messages.find((m) => m.operation === "initialize");
      // failed init (no analyzeSource capability)
      f0.onmessage({
        data: {
          canonical: true,
          requestId: im.requestId,
          responseJson: JSON.stringify({
            protocolVersion: 1,
            requestId: im.requestId,
            status: "failed",
            error: {
              code: "UnsupportedCapability",
              safeMessage: "No analyzer",
              retryable: false,
            },
          }),
        },
      });
      const r = await job;
      const analyzePosted = f0.messages.some(
        (m) => m.operation === "analyzeSource",
      );
      return {
        status: statusOf(r.responseJson),
        code: codeOf(r.responseJson),
        analyzePosted,
        epoch: mod.getWorkerEpoch(),
      };
    }
    case "initMalformedJson": {
      const packed = enc.encode("x");
      const job = mod.analyzeSource(buildMeta("imj", packed.length), packed);
      const f0 = fakes[0];
      f0.onmessage({ data: { id: f0.messages[0].id, result: true } });
      await yieldMicro();
      const im = f0.messages.find((m) => m.operation === "initialize");
      // malformed init responseJson (not valid JSON)
      f0.onmessage({
        data: {
          canonical: true,
          requestId: im.requestId,
          responseJson: "{not json",
        },
      });
      const r = await job;
      const analyzePosted = f0.messages.some(
        (m) => m.operation === "analyzeSource",
      );
      return {
        status: statusOf(r.responseJson),
        code: codeOf(r.responseJson),
        analyzePosted,
      };
    }
    case "initWrongId": {
      const packed = enc.encode("x");
      const job = mod.analyzeSource(buildMeta("iwi", packed.length), packed);
      const f0 = fakes[0];
      f0.onmessage({ data: { id: f0.messages[0].id, result: true } });
      await yieldMicro();
      const im = f0.messages.find((m) => m.operation === "initialize");
      // init reply carries a DIFFERENT requestId than the request
      f0.onmessage({
        data: {
          canonical: true,
          requestId: im.requestId,
          responseJson: JSON.stringify({
            protocolVersion: 1,
            requestId: "foreign-id",
            status: "completed",
            payload: {
              engineVersion: "fake",
              operations: ["analyzeSource"],
              limits: {},
            },
          }),
        },
      });
      const r = await job;
      const analyzePosted = f0.messages.some(
        (m) => m.operation === "analyzeSource",
      );
      return {
        status: statusOf(r.responseJson),
        code: codeOf(r.responseJson),
        analyzePosted,
      };
    }
    case "initValidCapability": {
      const packed = enc.encode("x");
      const job = mod.analyzeSource(buildMeta("ivc", packed.length), packed);
      const f0 = fakes[0];
      await driveReadyInit(f0);
      const am = f0.messages.find((m) => m.operation === "analyzeSource");
      f0.onmessage({
        data: {
          canonical: true,
          requestId: am.requestId,
          responseJson: analyzeCompletedJson(am.requestId),
        },
      });
      const r = await job;
      return { status: statusOf(r.responseJson) };
    }
    case "readyRejectionNoLeak": {
      // FakeWorker holds ready (the scenario never replies to it).
      const p = mod.analyzeSource(buildMeta("old", 1), enc.encode("x"));
      mod.cancelSource("old"); // reset -> worker1 terminated, epoch 1
      const q = mod.analyzeSource(buildMeta("new", 1), enc.encode("y")); // worker2 (fakes[1])
      await yieldMicro(); // old ready().catch fires — must NOT clear worker2's initialization
      mod.ready(); // bug: creates worker3; fix: returns worker2's ready (no new worker)
      const workerCount = fakes.length;
      mod.dispose();
      await p; // settles cancelled (cancel)
      await q; // settles cancelled (dispose)
      return { workerCount, disposed: fakes[1] && fakes[1].terminated };
    }
    case "constructorThrow": {
      // First `new Worker(...)` throws synchronously; subsequent ones succeed.
      let throwOnce = true;
      const Base = FakeWorker;
      window.Worker = class extends Base {
        constructor() {
          if (throwOnce) {
            throwOnce = false;
            throw new Error("ctor boom");
          }
          super();
          fakes.push(this);
        }
      };
      const r = await mod.analyzeSource(buildMeta("ct", 1), enc.encode("x"));
      // Restore the default fake worker for the next (healthy) call.
      window.Worker = class extends Base {
        constructor() {
          super();
          fakes.push(this);
        }
      };
      const q = mod.analyzeSource(buildMeta("ct2", 1), enc.encode("y"));
      const f0 = fakes[0]; // the healthy worker (second construction)
      await driveReadyInit(f0);
      const am = f0.messages.find((m) => m.operation === "analyzeSource");
      f0.onmessage({
        data: {
          canonical: true,
          requestId: am.requestId,
          responseJson: analyzeCompletedJson(am.requestId),
        },
      });
      const r2 = await q;
      // Yield through end-of-turn rejection reporting BEFORE checking.
      await yieldMicro();
      await new Promise((res) => setTimeout(res, 0));
      return {
        r1Status: statusOf(r.responseJson),
        r1Code: codeOf(r.responseJson),
        unhandled: unhandled.length,
        r2Status: statusOf(r2.responseJson),
      };
    }
    case "earlyCancelNoUnhandled": {
      // Early cancel/reset: ready is held (never replied), so cancelSource resets the
      // worker and the READY promise rejects via the .catch chain. This is the
      // path that produced a discarded rejecting .catch child (unhandled rejection)
      // before the chain fix; the chained p must be awaited by initDispatch.
      const p = mod.analyzeSource(buildMeta("ec", 1), enc.encode("x"));
      mod.cancelSource("ec"); // reset -> ready rejects via the chain
      const r = await p; // cancelled
      // Yield through end-of-turn rejection reporting BEFORE checking events.
      await yieldMicro();
      await new Promise((res) => setTimeout(res, 0));
      return { status: statusOf(r.responseJson), unhandled: unhandled.length };
    }
    default:
      throw new Error("unknown synthetic scenario " + scenarioId);
  }
}

async function runSynthetic(browser, scenarioId) {
  const page = await newPage(browser);
  await page.goto(APP_URL + "/", { waitUntil: "domcontentloaded" });
  const result = await page.evaluate(syntheticDriver, {
    scenarioId,
    deadlineMs: ANALYSIS_DEADLINE_MS,
  });
  await page.close();
  return result;
}

// A fake worker that posts TWO canonical completed replies for one analyzeSource
// (to verify the bridge settles a terminal outcome exactly once).
const DUPLICATE_WORKER = `
self.onmessage = ({ data }) => {
  if (data && typeof data.protocolVersion === "number") {
    if (data.operation === "initialize") {
      self.postMessage({ canonical: true, requestId: data.requestId, responseJson:
        JSON.stringify({ protocolVersion:1, requestId: data.requestId, status: "completed",
          payload: { engineVersion: "fake", operations: ["analyzeSource"], limits: {} } }) });
      return;
    }
    if (data.operation === "analyzeSource") {
      const ok = JSON.stringify({ protocolVersion:1, requestId: data.requestId, status: "completed",
        payload: { snapshotId: "snap-1", profile: { id: "engine-structural", version: "0" },
          facts: { files: [{ fileId: "f1", byteLength: 0, hasBom: false, newlineKind: "lf",
            truncated: false, braceDepthExceeded: false, tokenCount: 0, engineOutcome: "pass", tokens: [] }] },
          diagnostics: [], ruleResults: [{ ruleId: "ENGINE-STRUCT-001", origin: "engine", outcome: "pass",
            perFile: [{ fileId: "f1", outcome: "pass", diagnosticIds: [] }], limitations: [] }],
          aggregateOutcome: "pass",
          coverage: { factKinds: [], unsupported: [], truncatedFiles: [], diagnosticsTruncated: false, resultTruncated: false },
          unavailableCapabilities: ["pdfFacts", "pdfChecks"] } });
      self.postMessage({ canonical: true, requestId: data.requestId, responseJson: ok });
      self.postMessage({ canonical: true, requestId: data.requestId, responseJson: ok }); // duplicate late
      return;
    }
  }
  if (data.type === "ready") self.postMessage({ id: data.id, result: true });
};
`;

// A fake worker (SYNTHETIC, labeled) that echoes the received metadataJson in its
// canonical reply, so the test can assert the original workingGeneration
// above 2^53 arrived at the worker UNCHANGED (receive boundary). It does
// NOT run real WASM; the happy-path test separately runs real WASM.
const ECHO_METADATA_WORKER = `
self.onmessage = ({ data }) => {
  if (data && typeof data.protocolVersion === "number") {
    if (data.operation === "initialize") {
      self.postMessage({ canonical: true, requestId: data.requestId, responseJson:
        JSON.stringify({ protocolVersion:1, requestId: data.requestId, status: "completed",
          payload: { engineVersion: "fake-echo", operations: ["analyzeSource"], limits: {} } }) });
      return;
    }
    if (data.operation === "analyzeSource") {
      const meta = (data.payload && data.payload.metadataJson) || "";
      self.postMessage({ canonical: true, requestId: data.requestId, responseJson: meta });
      return;
    }
  }
  if (data.type === "ready") self.postMessage({ id: data.id, result: true });
};
`;

(async () => {
  const browser = await chromium.launch({ headless: true });
  try {
    await runHappyPath(browser);
    await runReceiveBoundaryExactness(browser);
    await runWorkerValidation(browser);
    await runCancelDuringReadiness(browser);
    await runInitConcurrency(browser);
    await runCancelInFlight(browser);
    await runDeadline(browser);
    await runCrash(browser);
    await runOnMessageError(browser);
    await runDuplicate(browser);
    // SYNTHETIC lifecycle scenarios (test-local fake Worker; NOT real WASM):
    await runLifecycleCompletedEpochRetainedOnDispose(browser);
    await runLifecycleStaleOldWorkerReplyIgnored(browser);
    await runLifecyclePostMessageThrowCleans(browser);
    await runLifecycleReadyFailureRetry(browser);
    await runLifecycleLegacyStaleReplyIgnored(browser);
    // Init validation (Fix 1): failed/malformed/wrong-id init must NOT dispatch analyze.
    await runInitFailedStatusNotPosted(browser);
    await runInitMalformedJsonNotPosted(browser);
    await runInitWrongIdNotPosted(browser);
    await runInitValidCapabilityDispatches(browser);
    await runFailedInitRecovery(browser);
    // Worker byte-view offset/length (Fix 2):
    await runWorkerByteView(browser);
    // Final initialization-race regressions (very small worker-bridge fixes).
    await runReadyRejectionNoLeak(browser);
    await runConstructorThrowNoUnhandled(browser);
    await runEarlyCancelNoUnhandled(browser);
    console.log(
      "analysis-worker-test passed: real Rust WASM happy/recovery + " +
        "deterministic cancel/timeout/crash/onmessageerror/duplicate/unsupported + " +
        "synthetic lifecycle (init-validation, epoch-capture, stale-reply, postMessage-throw, ready-retry, legacy-guard).",
    );
  } finally {
    await browser.close();
  }
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});

// ---- happy path: REAL WASM ----
async function runHappyPath(browser) {
  const page = await newPage(browser);
  await page.goto(APP_URL + "/", { waitUntil: "domcontentloaded" });
  const res = await page.evaluate(async () => {
    const mod = await import("/js/rust-bridge.js");
    const enc = new TextEncoder();
    async function analyze(text, requestId) {
      const packed = enc.encode(text);
      const meta = JSON.stringify({
        protocolVersion: 1,
        requestId,
        operation: "analyzeSource",
        payload: {
          snapshotId: "snap-1",
          projectId: "proj-1",
          sessionId: "sess-1",
          workingGeneration: 5,
          baseHeadRevisionId: null,
          profile: { id: "engine-structural", version: "0" },
          limits: {},
          files: [
            {
              fileId: "f1",
              relativePath: "main.tex",
              mediaKind: "text",
              hasBom: false,
              sha256: "x",
              byteOffset: 0,
              byteLength: packed.length,
            },
          ],
        },
      });
      return await mod.analyzeSource(meta, packed);
    }
    const out = {};
    out.unmatched = await analyze("line1\r\n\\foo {unmatched", "req-unmatched");
    out.matched = await analyze("a {b} c", "req-matched");
    out.zero = await analyze("", "req-zero");
    // caller Uint8Array stays attached and equal after transfer
    const caller = enc.encode("x{");
    const callerCopy = caller.slice(0);
    out.caller = await mod.analyzeSource(
      JSON.stringify({
        protocolVersion: 1,
        requestId: "req-caller",
        operation: "analyzeSource",
        payload: {
          snapshotId: "snap-1",
          projectId: "proj-1",
          sessionId: "sess-1",
          workingGeneration: 5,
          baseHeadRevisionId: null,
          profile: { id: "engine-structural", version: "0" },
          limits: {},
          files: [
            {
              fileId: "f1",
              relativePath: "main.tex",
              mediaKind: "text",
              hasBom: false,
              sha256: "x",
              byteOffset: 0,
              byteLength: caller.length,
            },
          ],
        },
      }),
      caller,
    );
    out.callerAttached =
      caller.buffer.byteLength > 0 &&
      caller.byteLength === callerCopy.byteLength;
    out.callerEqual = Array.from(caller).every((b, i) => b === callerCopy[i]);
    out.epochAfter = mod.getWorkerEpoch();
    return out;
  });

  const u = parseJson(res.unmatched.responseJson);
  assert.equal(u.status, "completed");
  assert.equal(u.payload.aggregateOutcome, "fail");
  const diag = u.payload.diagnostics.find(
    (d) => d.ruleId === "ENGINE-STRUCT-001",
  );
  assert.ok(diag, "expected an ENGINE-STRUCT-001 diagnostic");
  assert.equal(diag.fileId, "f1");
  assert.ok(diag.span.startByte < diag.span.endByte);
  assert.ok(
    diag.span.startByte >= "line1\r\n".length,
    "diagnostic lands after the first CRLF",
  );

  const m = parseJson(res.matched.responseJson);
  assert.equal(m.status, "completed");
  assert.equal(m.payload.aggregateOutcome, "pass");
  assert.equal(m.payload.diagnostics.length, 0);

  const z = parseJson(res.zero.responseJson);
  assert.equal(z.status, "completed");
  assert.equal(z.payload.aggregateOutcome, "pass");
  assert.equal(z.payload.diagnostics.length, 0);

  assert.equal(parseJson(res.caller.responseJson).status, "completed");
  assert.ok(res.callerAttached, "caller Uint8Array buffer stayed attached");
  assert.ok(res.callerEqual, "caller Uint8Array bytes unchanged");

  // Epoch-label invariant: the completed result carries the dispatch epoch;
  // after a later dispose/reset the current global epoch is higher, so the
  // captured epoch is no longer current (a stale pre-reset result is detectable).
  const capturedEpoch = res.unmatched.workerEpoch;
  assert.ok(typeof capturedEpoch === "number" && capturedEpoch >= 0);
  assert.equal(res.epochAfter, capturedEpoch); // no reset happened during happy path
  await page.close();
}

// ---- receive-boundary exactness (SYNTHETIC fake echoes metadata) ----
async function runReceiveBoundaryExactness(browser) {
  const page = await newPage(browser);
  await page.route("**/js/rust-worker.js", (route) =>
    route.fulfill({
      contentType: "text/javascript",
      body: ECHO_METADATA_WORKER,
    }),
  );
  await page.goto(APP_URL + "/", { waitUntil: "domcontentloaded" });
  const res = await page.evaluate(async (wgExact) => {
    const mod = await import("/js/rust-bridge.js");
    const packed = new TextEncoder().encode("{");
    // Build metadata with the exact large integer as a STRING literal.
    const meta = JSON.stringify({
      protocolVersion: 1,
      requestId: "req-echo",
      operation: "analyzeSource",
      payload: {
        snapshotId: "snap-1",
        projectId: "proj-1",
        sessionId: "sess-1",
        workingGeneration: Number(wgExact),
        baseHeadRevisionId: null,
        profile: { id: "engine-structural", version: "0" },
        limits: {},
        files: [
          {
            fileId: "f1",
            relativePath: "main.tex",
            mediaKind: "text",
            hasBom: false,
            sha256: "x",
            byteOffset: 0,
            byteLength: packed.length,
          },
        ],
      },
    }).replace(/"workingGeneration":[0-9]+/, `"workingGeneration":${wgExact}`);
    return {
      responseJson: (await mod.analyzeSource(meta, packed)).responseJson,
      metaSent: meta,
    };
  }, WG_EXACT);
  // SYNTHETIC (fake echo worker, NOT real WASM): the worker received the
  // metadataJson and echoed it back; assert the exact workingGeneration
  // above 2^53 arrived at the worker UNCHANGED (receive boundary, not just
  // the constructed string).
  assert.ok(
    res.metaSent.includes(`"workingGeneration":${WG_EXACT}`),
    "constructed metadata is exact",
  );
  assert.ok(
    res.responseJson.includes(`"workingGeneration":${WG_EXACT}`),
    "workingGeneration above 2^53 arrived at the worker boundary unchanged",
  );
  await page.close();
}

// ---- worker dispatch validation (REAL worker, direct) ----
async function runWorkerValidation(browser) {
  const page = await newPage(browser);
  await page.goto(APP_URL + "/", { waitUntil: "domcontentloaded" });
  const res = await page.evaluate(async (wgExact) => {
    const worker = new Worker("/js/rust-worker.js", { type: "module" });
    await new Promise((r) => {
      const h = () => {
        worker.removeEventListener("message", h);
        r();
      };
      worker.addEventListener("message", h);
      worker.postMessage({ id: 1, type: "ready" });
    });
    const out = {};
    const waitReply = () =>
      new Promise((resolve) => {
        const h = (ev) => {
          worker.removeEventListener("message", h);
          resolve(ev.data);
        };
        worker.addEventListener("message", h);
      });
    worker.postMessage({
      protocolVersion: 1,
      requestId: "r1",
      operation: "bogus",
      payload: {},
    });
    out.unknownOp = await waitReply();
    worker.postMessage({
      protocolVersion: 99,
      requestId: "r2",
      operation: "initialize",
      payload: {},
    });
    out.badProto = await waitReply();
    worker.postMessage({
      protocolVersion: "1",
      requestId: "r3",
      operation: "initialize",
      payload: {},
    });
    out.stringProto = await waitReply();
    const packed = new TextEncoder().encode("{");
    const meta = JSON.stringify({
      protocolVersion: 1,
      requestId: "r4",
      operation: "analyzeSource",
      payload: {
        snapshotId: "snap-1",
        projectId: "proj-1",
        sessionId: "sess-1",
        workingGeneration: Number(wgExact),
        baseHeadRevisionId: null,
        profile: { id: "engine-structural", version: "0" },
        limits: {},
        files: [
          {
            fileId: "f1",
            relativePath: "main.tex",
            mediaKind: "text",
            hasBom: false,
            sha256: "x",
            byteOffset: 0,
            byteLength: packed.length,
          },
        ],
      },
    }).replace(/"workingGeneration":[0-9]+/, `"workingGeneration":${wgExact}`);
    out.metaExact = meta.includes(`"workingGeneration":${wgExact}`);
    worker.postMessage(
      {
        protocolVersion: 1,
        requestId: "r4",
        operation: "analyzeSource",
        payload: { metadataJson: meta, packedBytes: packed.buffer },
      },
      [packed.buffer],
    );
    out.analyze = await waitReply();
    worker.terminate();
    return out;
  }, WG_EXACT);

  assert.equal(parseJson(res.unknownOp.responseJson).status, "failed");
  assert.equal(
    parseJson(res.unknownOp.responseJson).error.code,
    "UnsupportedCapability",
  );
  assert.equal(parseJson(res.badProto.responseJson).status, "failed");
  assert.equal(
    parseJson(res.badProto.responseJson).error.code,
    "UnsupportedProtocol",
  );
  // Route by PRESENCE of protocolVersion: a string protocolVersion reaches
  // canonical and is rejected as UnsupportedProtocol (not handled as legacy).
  assert.equal(parseJson(res.stringProto.responseJson).status, "failed");
  assert.equal(
    parseJson(res.stringProto.responseJson).error.code,
    "UnsupportedProtocol",
  );
  assert.ok(
    res.metaExact,
    "workingGeneration above 2^53 preserved as exact literal",
  );
  const an = parseJson(res.analyze.responseJson);
  assert.equal(an.status, "completed");
  assert.equal(an.payload.aggregateOutcome, "fail");
  await page.close();
}

// ---- cancel while readiness held (B4): entry registered before readiness ----
async function runCancelDuringReadiness(browser) {
  const page = await newPage(browser);
  await page.route("**/js/rust-worker.js", (route) =>
    route.fulfill({
      contentType: "text/javascript",
      body: INIT_COUNTING_WORKER(false),
    }),
  );
  await page.goto(APP_URL + "/", { waitUntil: "domcontentloaded" });
  const res = await page.evaluate(async () => {
    const mod = await import("/js/rust-bridge.js");
    const packed = new TextEncoder().encode("a {b} c");
    const meta = JSON.stringify({
      protocolVersion: 1,
      requestId: "req-pre-init-cancel",
      operation: "analyzeSource",
      payload: {
        snapshotId: "snap-1",
        projectId: "proj-1",
        sessionId: "sess-1",
        workingGeneration: 5,
        baseHeadRevisionId: null,
        profile: { id: "engine-structural", version: "0" },
        limits: {},
        files: [
          {
            fileId: "f1",
            relativePath: "main.tex",
            mediaKind: "text",
            hasBom: false,
            sha256: "x",
            byteOffset: 0,
            byteLength: packed.length,
          },
        ],
      },
    });
    // The accepted job is registered BEFORE readiness, so a cancel while the
    // worker holds `ready` settles promptly and prevents later dispatch.
    const job = mod.analyzeSource(meta, packed);
    const ack = mod.cancelSource("req-pre-init-cancel");
    return { result: await job, ack, epoch: mod.getWorkerEpoch() };
  });
  assert.equal(parseJson(res.result.responseJson).status, "cancelled");
  assert.equal(
    parseJson(res.ack).payload.cancelledTargetRequestId,
    "req-pre-init-cancel",
  );
  assert.ok(res.epoch >= 1, "cancel reset bumped the epoch");
  // SAME-page recovery: remove the synthetic route and run actual Rust WASM.
  await page.unroute("**/js/rust-worker.js");
  const recovery = await recoverRealWasm(page, "req-pre-init-recover");
  assert.equal(parseJson(recovery.responseJson).status, "completed");
  assert.ok(
    recovery.workerEpoch >= res.epoch,
    "recovery runs on the post-reset worker epoch",
  );
  await page.close();
}

// ---- initialization concurrency (B5): single-flight initialize ----
// ---- initialization concurrency (B5): single-flight initialize ----
// SYNTHETIC (test-local fake Worker, NOT real WASM): await BOTH analyze
// arrivals then assert TOTAL initialize count is 1 (detects a broken
// single-flight that sends two inits; the prior version resolved on the first
// init message and could not detect a later second init).
async function runInitConcurrency(browser) {
  const out = await runSynthetic(browser, "initConcurrency");
  assert.equal(
    out.initCount,
    1,
    `expected one shared initialize, got ${out.initCount}`,
  );
  assert.equal(
    out.analyzeCount,
    2,
    `expected two analyzeSource dispatches, got ${out.analyzeCount}`,
  );
}

// ---- cancel an in-flight analyze (fake nonresponsive) + recovery ----
async function runCancelInFlight(browser) {
  const page = await newPage(browser);
  await page.route("**/js/rust-worker.js", (route) =>
    route.fulfill({
      contentType: "text/javascript",
      body: NONRESPONSIVE_WORKER,
    }),
  );
  await page.goto(APP_URL + "/", { waitUntil: "domcontentloaded" });
  const res = await page.evaluate(async () => {
    const mod = await import("/js/rust-bridge.js");
    const packed = new TextEncoder().encode("a {b} c");
    const meta = JSON.stringify({
      protocolVersion: 1,
      requestId: "req-inflight-cancel",
      operation: "analyzeSource",
      payload: {
        snapshotId: "snap-1",
        projectId: "proj-1",
        sessionId: "sess-1",
        workingGeneration: 5,
        baseHeadRevisionId: null,
        profile: { id: "engine-structural", version: "0" },
        limits: {},
        files: [
          {
            fileId: "f1",
            relativePath: "main.tex",
            mediaKind: "text",
            hasBom: false,
            sha256: "x",
            byteOffset: 0,
            byteLength: packed.length,
          },
        ],
      },
    });
    const job = mod.analyzeSource(meta, packed);
    // Let readiness+initialize settle so the analyze is dispatched (pending),
    // using a zero-delay macrotask yield (cooperative, not a timing sleep).
    await new Promise((r) => setTimeout(r, 0));
    const ack = mod.cancelSource("req-inflight-cancel");
    return { result: await job, ack };
  });
  assert.equal(parseJson(res.result.responseJson).status, "cancelled");
  assert.equal(
    parseJson(res.ack).payload.cancelledTargetRequestId,
    "req-inflight-cancel",
  );
  await page.unroute("**/js/rust-worker.js");
  const recovery = await recoverRealWasm(page, "req-inflight-recover");
  assert.equal(parseJson(recovery.responseJson).status, "completed");
  await page.close();
}

// ---- whole-job deadline timeout (fake nonresponsive) + recovery ----
async function runDeadline(browser) {
  const page = await newPage(browser);
  await page.route("**/js/rust-worker.js", (route) =>
    route.fulfill({
      contentType: "text/javascript",
      body: NONRESPONSIVE_WORKER,
    }),
  );
  await page.goto(APP_URL + "/", { waitUntil: "domcontentloaded" });
  const start = Date.now();
  const res = await page.evaluate(async () => {
    const mod = await import("/js/rust-bridge.js");
    const packed = new TextEncoder().encode("hangs {");
    const meta = JSON.stringify({
      protocolVersion: 1,
      requestId: "req-deadline",
      operation: "analyzeSource",
      payload: {
        snapshotId: "snap-1",
        projectId: "proj-1",
        sessionId: "sess-1",
        workingGeneration: 5,
        baseHeadRevisionId: null,
        profile: { id: "engine-structural", version: "0" },
        limits: {},
        files: [
          {
            fileId: "f1",
            relativePath: "main.tex",
            mediaKind: "text",
            hasBom: false,
            sha256: "x",
            byteOffset: 0,
            byteLength: packed.length,
          },
        ],
      },
    });
    return await mod.analyzeSource(meta, packed);
  });
  const elapsed = Date.now() - start;
  assert.equal(parseJson(res.responseJson).status, "failed");
  assert.equal(parseJson(res.responseJson).error.code, "Timeout");
  assert.ok(
    elapsed >= ANALYSIS_DEADLINE_MS && elapsed < ANALYSIS_DEADLINE_MS + 4000,
    `deadline elapsed ${elapsed}ms not near ${ANALYSIS_DEADLINE_MS}ms`,
  );
  await page.unroute("**/js/rust-worker.js");
  const recovery = await recoverRealWasm(page, "req-deadline-recover");
  assert.equal(parseJson(recovery.responseJson).status, "completed");
  await page.close();
}

// ---- worker crash (synchronous throw -> onerror) + recovery ----
async function runCrash(browser) {
  const page = await newPage(browser);
  await page.route("**/js/rust-worker.js", (route) =>
    route.fulfill({ contentType: "text/javascript", body: CRASH_WORKER }),
  );
  await page.goto(APP_URL + "/", { waitUntil: "domcontentloaded" });
  const res = await page.evaluate(async () => {
    const mod = await import("/js/rust-bridge.js");
    const packed = new TextEncoder().encode("crash {");
    const meta = JSON.stringify({
      protocolVersion: 1,
      requestId: "req-crash",
      operation: "analyzeSource",
      payload: {
        snapshotId: "snap-1",
        projectId: "proj-1",
        sessionId: "sess-1",
        workingGeneration: 5,
        baseHeadRevisionId: null,
        profile: { id: "engine-structural", version: "0" },
        limits: {},
        files: [
          {
            fileId: "f1",
            relativePath: "main.tex",
            mediaKind: "text",
            hasBom: false,
            sha256: "x",
            byteOffset: 0,
            byteLength: packed.length,
          },
        ],
      },
    });
    return await mod.analyzeSource(meta, packed);
  });
  assert.equal(parseJson(res.responseJson).status, "failed");
  assert.equal(parseJson(res.responseJson).error.code, "WorkerCrashed");
  await page.unroute("**/js/rust-worker.js");
  const recovery = await recoverRealWasm(page, "req-crash-recover");
  assert.equal(parseJson(recovery.responseJson).status, "completed");
  await page.close();
}

// ---- onmessageerror (unserializable reply) + recovery ----
// ---- onmessageerror: exercise the ACTUAL installed onmessageerror callback ----
// A test-local RecordingWorker extends the real Worker and records messages,
// holding the analyzeSource forward so the job stays pending. We then dispatch
// a SYNTHETIC MessageEvent("messageerror") on the real worker object so the
// bridge's REAL onmessageerror handler runs (this is distinct from an actual
// worker crash, which fires onerror — see runCrash). Recovery on the SAME
// bridge then runs real Rust WASM (hold released).
async function runOnMessageError(browser) {
  const page = await newPage(browser);
  await page.goto(APP_URL + "/", { waitUntil: "domcontentloaded" });
  const res = await page.evaluate(async () => {
    const RealWorker = window.Worker;
    const fakes = [];
    let holdAnalyze = true;
    window.Worker = class extends RealWorker {
      constructor(u, o) {
        super(u, o);
        fakes.push(this);
        this.messages = [];
        const orig = this.postMessage.bind(this);
        this.postMessage = (m, _t) => {
          this.messages.push(m);
          if (holdAnalyze && m && m.operation === "analyzeSource") return; // hold: never forwarded
          orig(m, _t);
        };
      }
    };
    const mod = await import("/js/rust-bridge.js");
    const enc = new TextEncoder();
    function meta(rid, n) {
      return JSON.stringify({
        protocolVersion: 1,
        requestId: rid,
        operation: "analyzeSource",
        payload: {
          snapshotId: "snap-1",
          projectId: "proj-1",
          sessionId: "sess-1",
          workingGeneration: 5,
          baseHeadRevisionId: null,
          profile: { id: "engine-structural", version: "0" },
          limits: {},
          files: [
            {
              fileId: "f1",
              relativePath: "main.tex",
              mediaKind: "text",
              hasBom: false,
              sha256: "x",
              byteOffset: 0,
              byteLength: n,
            },
          ],
        },
      });
    }
    const packed = enc.encode("err {");
    const job = mod.analyzeSource(meta("req-onmsgerr", packed.length), packed);
    // Wait until the analyze is dispatched (recorded, held) on the real worker.
    await new Promise((resolve, reject) => {
      const t = setInterval(() => {
        if (
          fakes[0] &&
          fakes[0].messages.some((m) => m && m.operation === "analyzeSource")
        ) {
          clearInterval(t);
          resolve();
        }
      }, 0);
      setTimeout(() => {
        clearInterval(t);
        reject(new Error("analyze never dispatched"));
      }, 5000);
    });
    // SYNTHETIC: invoke the bridge's installed onmessageerror handler directly with a
    // synthetic MessageEvent (its REAL handler runs; distinct from an actual
    // worker crash, which fires onerror - see runCrash).
    fakes[0].onmessageerror(new MessageEvent("messageerror"));
    const r = await job;
    // Recovery on the SAME bridge: allow the next analyze to reach real WASM.
    holdAnalyze = false;
    const packed2 = enc.encode("real {x");
    const recovery = await mod.analyzeSource(
      meta("req-onmsgerr-rec", packed2.length),
      packed2,
    );
    const st = (s) => {
      try {
        return JSON.parse(s);
      } catch {
        return {};
      }
    };
    return {
      status: st(r.responseJson).status,
      code: st(r.responseJson).error?.code,
      recStatus: st(recovery.responseJson).status,
    };
  });
  assert.equal(res.status, "failed");
  assert.equal(res.code, "WorkerCrashed");
  assert.equal(res.recStatus, "completed");
  await page.close();
}
async function runDuplicate(browser) {
  const page = await newPage(browser);
  await page.route("**/js/rust-worker.js", (route) =>
    route.fulfill({ contentType: "text/javascript", body: DUPLICATE_WORKER }),
  );
  await page.goto(APP_URL + "/", { waitUntil: "domcontentloaded" });
  const res = await page.evaluate(async () => {
    const mod = await import("/js/rust-bridge.js");
    const packed = new TextEncoder().encode("dup {");
    const meta = JSON.stringify({
      protocolVersion: 1,
      requestId: "req-dup",
      operation: "analyzeSource",
      payload: {
        snapshotId: "snap-1",
        projectId: "proj-1",
        sessionId: "sess-1",
        workingGeneration: 5,
        baseHeadRevisionId: null,
        profile: { id: "engine-structural", version: "0" },
        limits: {},
        files: [
          {
            fileId: "f1",
            relativePath: "main.tex",
            mediaKind: "text",
            hasBom: false,
            sha256: "x",
            byteOffset: 0,
            byteLength: packed.length,
          },
        ],
      },
    });
    return await mod.analyzeSource(meta, packed);
  });
  const env = parseJson(res.responseJson);
  // The duplicate late reply is ignored; the first completed settles once.
  assert.equal(env.status, "completed");
  assert.equal(env.payload.aggregateOutcome, "pass");
  await page.close();
}

// ---- SYNTHETIC lifecycle scenarios (test-local fake Worker; NOT real WASM) ----
// These drive the bridge's REAL routing/lifecycle logic with a fake worker whose
// onmessage/onerror/onmessageerror the scenario dispatches, so races are
// deterministic (no real-WASM timing). Each is labeled synthetic.

// L1: a completed result retains the dispatch epoch (0) even if the bridge is
// disposed synchronously before the promise callback runs (current epoch 1).
async function runLifecycleCompletedEpochRetainedOnDispose(browser) {
  const out = await runSynthetic(browser, "completedEpochRetained");
  assert.equal(out.status, "completed");
  assert.equal(out.workerEpoch, 0, "completed retains the captured epoch 0");
  assert.equal(out.currentEpoch, 1, "dispose bumped the current epoch to 1");
}

// L2: a stale reply from an old (terminated) worker cannot settle a fresh job
// on the new worker (captured-epoch guard).
async function runLifecycleStaleOldWorkerReplyIgnored(browser) {
  const out = await runSynthetic(browser, "staleOldWorkerReply");
  assert.equal(out.r1Epoch, 0);
  assert.equal(out.r2Status, "completed");
  assert.equal(
    out.r2Epoch,
    1,
    "new job settled on the post-reset worker epoch, not the stale 0",
  );
}

// L3: a synchronous postMessage throw for an analyze cleans pending/timers and
// a healthy new job survives past the old deadline (no orphan 5s timer).
async function runLifecyclePostMessageThrowCleans(browser) {
  const out = await runSynthetic(browser, "postMessageThrow");
  assert.equal(out.r1Status, "failed");
  assert.equal(out.r1Code, "WorkerCrashed");
  assert.equal(out.r2Status, "completed");
  assert.equal(
    out.currentEpoch,
    1,
    "no orphan timer fired after the old deadline (epoch unchanged)",
  );
}

// L4: a legacy ready failure is not permanently cached; a retry initializes a fresh worker.
async function runLifecycleReadyFailureRetry(browser) {
  const out = await runSynthetic(browser, "readyFailureRetry");
  assert.equal(out.r1Status, "failed");
  assert.equal(out.r1Code, "WorkerCrashed");
  assert.equal(
    out.r2Status,
    "completed",
    "retry on a fresh worker succeeded (not a permanently cached rejection)",
  );
}

// L5: a stale LEGACY reply from an old worker for a NEW pending legacy id is
// dropped (captured-epoch guard on the legacy onmessage closure); the new
// ready resolves properly on the fresh worker.
async function runLifecycleLegacyStaleReplyIgnored(browser) {
  const out = await runSynthetic(browser, "legacyStaleReply");
  assert.equal(out.cw1, 5);
  assert.equal(out.cw2, 7);
  assert.equal(
    out.staleDropped,
    true,
    "stale legacy reply from the old worker was ignored",
  );
}

// ---- init validation (Fix 1): failed/malformed/wrong-id init must NOT dispatch analyze ----
// SYNTHETIC (fake Worker): analyzeSource is never posted after a failed init.
async function runInitFailedStatusNotPosted(browser) {
  const out = await runSynthetic(browser, "initFailedStatus");
  assert.equal(out.status, "failed");
  assert.equal(out.code, "UnsupportedCapability");
  assert.equal(
    out.analyzePosted,
    false,
    "analyzeSource was NOT posted after a failed init",
  );
  assert.ok(out.epoch >= 1, "failed init reset the worker");
}

async function runInitMalformedJsonNotPosted(browser) {
  const out = await runSynthetic(browser, "initMalformedJson");
  assert.equal(out.status, "failed");
  assert.equal(out.code, "InvalidInput");
  assert.equal(
    out.analyzePosted,
    false,
    "analyzeSource was NOT posted after malformed init",
  );
}

async function runInitWrongIdNotPosted(browser) {
  const out = await runSynthetic(browser, "initWrongId");
  assert.equal(out.status, "failed");
  assert.equal(out.code, "InvalidInput");
  assert.equal(
    out.analyzePosted,
    false,
    "analyzeSource was NOT posted after a wrong-id init",
  );
}

async function runInitValidCapabilityDispatches(browser) {
  const out = await runSynthetic(browser, "initValidCapability");
  assert.equal(
    out.status,
    "completed",
    "a valid init with analyzeSource capability DOES dispatch",
  );
}

// A fake worker (route) that replies legacy ready + a FAILED canonical initialize,
// and counts analyzeSource posts via a same-origin BroadcastChannel. If the
// bridge incorrectly dispatched analyze after a failed init, the channel would
// record an "analyze" marker.
const FAILED_INIT_WORKER = `
const bc = new BroadcastChannel("oopAnalyzePosted");
self.onmessage = ({ data }) => {
  if (data && typeof data.protocolVersion === "number") {
    if (data.operation === "initialize") {
      bc.postMessage("init");
      self.postMessage({ canonical: true, requestId: data.requestId, responseJson:
        JSON.stringify({ protocolVersion:1, requestId: data.requestId, status: "failed",
          error: { code: "UnsupportedCapability", safeMessage: "No analyzer", retryable: false } }) });
      return;
    }
    if (data.operation === "analyzeSource") {
      bc.postMessage("analyze");
      self.postMessage({ canonical: true, requestId: data.requestId, responseJson:
        JSON.stringify({ protocolVersion:1, requestId: data.requestId, status: "completed",
          payload: { snapshotId: "snap-1", profile: { id: "engine-structural", version: "0" },
            facts: { files: [{ fileId: "f1", byteLength: 0, hasBom: false, newlineKind: "lf",
              truncated: false, braceDepthExceeded: false, tokenCount: 0, engineOutcome: "pass", tokens: [] }] },
            diagnostics: [], ruleResults: [{ ruleId: "ENGINE-STRUCT-001", origin: "engine", outcome: "pass",
              perFile: [{ fileId: "f1", outcome: "pass", diagnosticIds: [] }], limitations: [] }],
            aggregateOutcome: "pass",
            coverage: { factKinds: [], unsupported: [], truncatedFiles: [], diagnosticsTruncated: false, resultTruncated: false },
            unavailableCapabilities: ["pdfFacts", "pdfChecks"] } }) });
      return;
    }
  }
  if (data.type === "ready") self.postMessage({ id: data.id, result: true });
};
`;

// Failed init + SAME-bridge recovery with ACTUAL Rust WASM.
async function runFailedInitRecovery(browser) {
  const page = await newPage(browser);
  await page.route("**/js/rust-worker.js", (route) =>
    route.fulfill({ contentType: "text/javascript", body: FAILED_INIT_WORKER }),
  );
  await page.goto(APP_URL + "/", { waitUntil: "domcontentloaded" });
  const res = await page.evaluate(async () => {
    const bc = new BroadcastChannel("oopAnalyzePosted");
    const posted = [];
    bc.onmessage = (ev) => {
      posted.push(ev.data);
    };
    const mod = await import("/js/rust-bridge.js");
    const enc = new TextEncoder();
    const packed = enc.encode("x {unmatched");
    const meta = JSON.stringify({
      protocolVersion: 1,
      requestId: "req-failed-init",
      operation: "analyzeSource",
      payload: {
        snapshotId: "snap-1",
        projectId: "proj-1",
        sessionId: "sess-1",
        workingGeneration: 5,
        baseHeadRevisionId: null,
        profile: { id: "engine-structural", version: "0" },
        limits: {},
        files: [
          {
            fileId: "f1",
            relativePath: "main.tex",
            mediaKind: "text",
            hasBom: false,
            sha256: "x",
            byteOffset: 0,
            byteLength: packed.length,
          },
        ],
      },
    });
    const r = await mod.analyzeSource(meta, packed);
    const st = (s) => {
      try {
        return JSON.parse(s);
      } catch {
        return {};
      }
    };
    return {
      status: st(r.responseJson).status,
      code: st(r.responseJson).error?.code,
      posted,
    };
  });
  assert.equal(res.status, "failed");
  assert.equal(res.code, "UnsupportedCapability");
  assert.ok(
    !res.posted.includes("analyze"),
    "analyzeSource was NOT posted to the worker after a failed init",
  );
  // SAME-bridge recovery with actual Rust WASM (the failed init reset the worker; a fresh request re-initializes).
  await page.unroute("**/js/rust-worker.js");
  const recovery = await recoverRealWasm(page, "req-failed-init-recover");
  assert.equal(parseJson(recovery.responseJson).status, "completed");
  await page.close();
}

// ---- worker byte-view offset/length (Fix 2): direct worker, REAL WASM ----
// Post analyzeSource with packedBytes = a SUBARRAY of a buffer that has
// surrounding junk, so a whole-buffer bug would analyze the junk (unmatched)
// while a correct byte view analyzes only the selected span (matched). The
// metadata describes the sub-slice (byteOffset 0, byteLength = sub-slice length).
async function runWorkerByteView(browser) {
  const page = await newPage(browser);
  await page.goto(APP_URL + "/", { waitUntil: "domcontentloaded" });
  const res = await page.evaluate(async () => {
    const worker = new Worker("/js/rust-worker.js", { type: "module" });
    await new Promise((r) => {
      const h = () => {
        worker.removeEventListener("message", h);
        r();
      };
      worker.addEventListener("message", h);
      worker.postMessage({ id: 1, type: "ready" });
    });
    const waitReply = () =>
      new Promise((resolve) => {
        const h = (ev) => {
          worker.removeEventListener("message", h);
          resolve(ev.data);
        };
        worker.addEventListener("message", h);
      });
    // Surround the intended span with an UNMATCHED bracket so a whole-buffer bug
    // would yield aggregateOutcome=fail; the sub-slice "{x}" is matched -> pass.
    const buf = new TextEncoder().encode("[{x}");
    const sub = buf.subarray(1, 4); // "{x}" (byteOffset 1, byteLength 3)
    const meta = JSON.stringify({
      protocolVersion: 1,
      requestId: "bv1",
      operation: "analyzeSource",
      payload: {
        snapshotId: "snap-1",
        projectId: "proj-1",
        sessionId: "sess-1",
        workingGeneration: 5,
        baseHeadRevisionId: null,
        profile: { id: "engine-structural", version: "0" },
        limits: {},
        files: [
          {
            fileId: "f1",
            relativePath: "main.tex",
            mediaKind: "text",
            hasBom: false,
            sha256: "x",
            byteOffset: 0,
            byteLength: sub.length,
          },
        ],
      },
    });
    worker.postMessage(
      {
        protocolVersion: 1,
        requestId: "bv1",
        operation: "analyzeSource",
        payload: { metadataJson: meta, packedBytes: sub },
      },
      [sub.buffer],
    );
    const reply = await waitReply();
    worker.terminate();
    return reply;
  });
  const an = parseJson(res.responseJson);
  assert.equal(an.status, "completed");
  assert.equal(
    an.payload.aggregateOutcome,
    "pass",
    "the selected sub-slice (matched braces) reached WASM, not the whole buffer",
  );
  assert.equal(
    an.payload.facts.files[0].byteLength,
    3,
    "byte view respected byteOffset/byteLength",
  );
  await page.close();
}

// ---- initialization race regressions (final review round) ----
// SYNTHETIC: an OLD ready rejection must NOT clear a NEW initialization cache (no
// leaked third worker), and final disposal terminates the current worker.
async function runReadyRejectionNoLeak(browser) {
  const out = await runSynthetic(browser, "readyRejectionNoLeak");
  assert.equal(
    out.workerCount,
    2,
    `expected exactly two workers (no leaked third), got ${out.workerCount}`,
  );
  assert.equal(
    out.disposed,
    true,
    "final disposal terminated the current worker",
  );
}

// SYNTHETIC: a Worker constructor throw returns a typed Failed with ZERO
// unhandled rejections (ensureInitialized returns its LOCAL promise), and the
// next SAME-bridge call initializes a healthy worker.
async function runConstructorThrowNoUnhandled(browser) {
  const out = await runSynthetic(browser, "constructorThrow");
  assert.equal(out.r1Status, "failed");
  assert.equal(out.r1Code, "WorkerCrashed");
  assert.equal(
    out.unhandled,
    0,
    "no unhandled rejection from the failed init promise",
  );
  assert.equal(
    out.r2Status,
    "completed",
    "next same-bridge call initialized a healthy worker",
  );
}

// SYNTHETIC: early cancel/reset rejects the READY promise via the .catch chain.
// Before the chain fix the discarded .catch child rejected unawaited (unhandled
// rejection). The listener is installed BEFORE the cancellation; the scenario
// yields through end-of-turn reporting before checking. Zero unhandled required.
async function runEarlyCancelNoUnhandled(browser) {
  const out = await runSynthetic(browser, "earlyCancelNoUnhandled");
  assert.equal(out.status, "cancelled");
  assert.equal(
    out.unhandled,
    0,
    "the ready rejection is awaited via the chained promise (no detached rejecting child)",
  );
}

// ---- helpers ----

// Guarded JSON parse: a malformed bridge response fails the test with a
// clear message instead of a raw SyntaxError stack.
function parseJson(s) {
  try {
    return JSON.parse(s);
  } catch (e) {
    throw new Error(
      `invalid JSON response: ${e.message}: ${String(s).slice(0, 200)}`,
    );
  }
}

// SAME-page recovery: remove the synthetic route and run actual Rust WASM again
// on the SAME bridge, asserting a correct diagnostic and a changed epoch.
async function recoverRealWasm(page, requestId) {
  return await page.evaluate(async (rid) => {
    const mod =
      window.__mod || (window.__mod = await import("/js/rust-bridge.js"));
    const packed = new TextEncoder().encode("real {x");
    const meta = JSON.stringify({
      protocolVersion: 1,
      requestId: rid,
      operation: "analyzeSource",
      payload: {
        snapshotId: "snap-1",
        projectId: "proj-1",
        sessionId: "sess-1",
        workingGeneration: 5,
        baseHeadRevisionId: null,
        profile: { id: "engine-structural", version: "0" },
        limits: {},
        files: [
          {
            fileId: "f1",
            relativePath: "main.tex",
            mediaKind: "text",
            hasBom: false,
            sha256: "x",
            byteOffset: 0,
            byteLength: packed.length,
          },
        ],
      },
    });
    const r = await mod.analyzeSource(meta, packed);
    return { responseJson: r.responseJson, workerEpoch: r.workerEpoch };
  }, requestId);
}

async function newPage(browser) {
  const context = await browser.newContext();
  const page = await context.newPage();
  const origClose = page.close.bind(page);
  page.close = async () => {
    await origClose();
    await context.close();
  };
  return page;
}
