"use strict";
const fs = require("node:fs");
const path = require("node:path");
const assert = require("node:assert/strict");
const { Worker } = require("node:worker_threads");
const crypto = require("node:crypto");
const candidate = process.env.CANDIDATE || "/control/candidate";
const label = process.env.CANCEL_LABEL || "cancellation01";
const results = process.env.RESULTS || "/control/results";
const sha = (bytes) => crypto.createHash("sha256").update(bytes).digest("hex");
(async () => {
  const started = Date.now();
  const worker = new Worker(path.join(__dirname, "test-engine.cjs"), {
    argv: ["latex", label + "-runaway"],
    env: {
      ...process.env,
      SOURCE_FILE: path.join(__dirname, "tests/runaway.tex"),
    },
    resourceLimits: { maxOldGenerationSizeMb: 1024 },
  });
  const limit = setTimeout(() => {
    void worker.terminate();
  }, 15000);
  let reachedRunaway = false;
  let killedAt = 0;
  worker.on("message", (message) => {
    if (message.runaway) {
      reachedRunaway = true;
      setTimeout(() => {
        killedAt = Date.now();
        void worker.terminate();
      }, 250);
    }
  });
  const exit = await new Promise((resolve, reject) => {
    worker.once("exit", resolve);
    worker.once("error", reject);
  });
  clearTimeout(limit);
  assert.ok(
    reachedRunaway,
    "actual infinite Lua loop reached before cancellation",
  );
  assert.ok(killedAt > 0, "physical termination requested");
  assert.equal(exit, 1);
  assert.ok(!fs.existsSync(path.join(results, label + "-runaway.pdf")));
  const result = {
    passed: true,
    enteredActualLuaLoop: true,
    physicalWorkerTermination: true,
    terminationMs: Date.now() - killedAt,
    totalMs: Date.now() - started,
    exit,
    noResult: true,
    wasmSha256: sha(fs.readFileSync(candidate + "/luatex.wasm")),
    loaderSha256: sha(fs.readFileSync(candidate + "/luatex.js")),
  };
  fs.writeFileSync(
    path.join(results, label + ".json"),
    JSON.stringify(result, null, 2) + "\n",
  );
  console.log(JSON.stringify(result));
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
