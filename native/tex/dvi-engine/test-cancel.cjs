// Worker destruction interrupts real synchronous converter work. Browser
// request/revision cancellation orchestration remains the master's integration.
const {Worker, isMainThread, parentPort, workerData} = require('node:worker_threads');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const {convert} = require('./convert-fixture.cjs');
if (!isMainThread) {
  convert(workerData.candidate, workerData.resources, workerData.fixture, () => parentPort.postMessage('entering-native'))
    .then(() => parentPort.postMessage('unexpected-completed'))
    .catch(() => parentPort.postMessage('unexpected-failure'));
} else {
  const [candidate, resources, fixture, output] = process.argv.slice(2).map(p => path.resolve(p));
  const w = new Worker(__filename, {workerData: {candidate, resources, fixture}});
  const timer = setTimeout(() => { console.error('Cancellation test timed out'); w.terminate(); process.exitCode = 1; }, 30000);
  w.once('error', e => { clearTimeout(timer); console.error(e); process.exitCode = 1; });
  w.once('message', async message => {
    assert.equal(message, 'entering-native');
    // Native entry follows the message synchronously in the worker. Wait 20ms
    // so termination targets real engine work, not merely WASM initialization.
    await new Promise(resolve => setTimeout(resolve, 20));
    const exit = await w.terminate();
    clearTimeout(timer);
    const receipt = {passed: true, mechanism: 'worker destruction during real 60000-page conversion', exit};
    fs.writeFileSync(output, JSON.stringify(receipt, null, 2) + '\n');
    console.log(receipt);
  });
  w.on('message', message => { if (message !== 'entering-native') { console.error(message); process.exitCode = 1; } });
}
