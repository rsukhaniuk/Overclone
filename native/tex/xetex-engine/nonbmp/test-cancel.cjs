"use strict";
// Actual native write16 -> JS import is the in-WASM witness before termination.
const {Worker} = require("node:worker_threads");
const {once} = require("node:events");
const fs = require("node:fs"), path = require("node:path"), assert = require("node:assert/strict");
const out = path.join(process.env.RESULTS, "cancellation");
fs.mkdirSync(out, {recursive: true});
function worker(mode) {
  return new Worker(path.join(__dirname, "test-runtime.cjs"), {
    argv: [mode], env: {...process.env, RESULTS: out}, stdout: true, stderr: true,
  });
}
(async () => {
  const deadline = setTimeout(() => { throw new Error("Native cancellation/recovery deadline"); }, 45000);
  const spinning = worker("runaway");
  const [witness] = await once(spinning, "message");
  assert.equal(witness, "runaway", "must witness actual native RUNAWAY-BEGIN write");
  const before = performance.now(), exit = await spinning.terminate();
  const elapsed = performance.now() - before;
  assert.equal(exit, 1);
  assert.ok(elapsed >= 0 && elapsed < 10000);
  assert.ok(!fs.existsSync(path.join(out, "runaway.json")));
  const recovery = worker("positive");
  assert.equal((await once(recovery, "exit"))[0], 0);
  const receipt = JSON.parse(fs.readFileSync(path.join(out, "positive.json")));
  assert.equal(receipt.status, "pass");
  assert.ok(receipt.rawMapBytes > 0);
  const result = {status: "pass", actualNativeWriteWitness: true,
    physicalWorkerExit: exit, terminationMilliseconds: elapsed,
    noLateTerminalReceipt: true, freshWorkerRecovery: true, identities: receipt.identities};
  fs.writeFileSync(path.join(out, "receipt.json"), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result));
  clearTimeout(deadline);
})().catch(error => { console.error(error); process.exitCode = 1; });
