// Node Worker termination/recovery test over the genuine WASM engine.
// Published browser cancellation remains the master's integration check.
const {Worker} = require('node:worker_threads');
const {once} = require('node:events');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const out = '/control/results/cancel';
fs.mkdirSync(out, {recursive: true});
for (const name of ['xelatex.fmt', 'pass1-головний.aux']) fs.copyFileSync('/control/results/' + name, path.join(out, name));
function worker(mode) {
  return new Worker(path.join(__dirname, 'test.cjs'), {
    argv: [mode], env: {...process.env, XETEX_RESULTS: out,
      XETEX_TEXDIR: '/control/build/xetex/subset'}, stdout: true, stderr: true,
  });
}
(async () => {
  const deadline = setTimeout(() => { throw new Error('Cancellation test timed out'); }, 60000);
  deadline.unref();
  const spinning = worker('spin');
  const [message] = await once(spinning, 'message');
  assert.equal(message.compilationStarted, true);
  const started = Date.now();
  const code = await spinning.terminate();
  const terminationMilliseconds = Date.now()-started;
  assert.equal(code, 1);
  assert.ok(!fs.existsSync(path.join(out, 'spin.json')), 'no late terminal compile artifact');
  const recovery = worker('pass2');
  const [recoveryExit] = await once(recovery, 'exit');
  assert.equal(recoveryExit, 0);
  let report;
  try { report = JSON.parse(fs.readFileSync(path.join(out, 'pass2.json'))); }
  catch (error) { throw new Error('Invalid recovery receipt', {cause: error}); }
  assert.equal(report.status, 0);
  const result = {terminatedExit: code, terminationMilliseconds,
    freshWorkerExit: recoveryExit, freshWorkerXdv: report.files['головний.xdv']};
  fs.writeFileSync(path.join(out, 'cancellation.json'), JSON.stringify(result, null, 2) + '\n');
  console.log(JSON.stringify(result));
  clearTimeout(deadline);
})().catch(error => { console.error(error); process.exitCode = 1; });
