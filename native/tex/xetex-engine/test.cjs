// Private real-engine test harness; not application/runtime worker glue.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const build = process.env.XETEX_BUILD || '/control/build/xetex';
const out = process.env.XETEX_RESULTS || '/control/results';
const mode = process.argv[2] || 'format';
const create = require(path.join(build, 'xetex.js'));
const digest = b => crypto.createHash('sha256').update(b).digest('hex');
(async () => {
  let log = '', truncated = false;
  const logPath = path.join(out, mode + '.log');
  fs.writeFileSync(logPath, '');
  const print = s => {
    const text = String(s) + '\n';
    if (log.length + text.length <= 2 * 1024 * 1024) {
      log += text;
      fs.appendFileSync(logPath, text);
    } else truncated = true;
  };
  const m = await create({wasmBinary: fs.readFileSync(path.join(build, 'xetex.wasm')), print, printErr: print});
  const F = m.FS;
  F.mkdir('/tex'); F.mkdir('/work'); F.chdir('/work');
  const texdir = process.env.XETEX_TEXDIR || path.join(build, 'tex');
  for (const name of fs.readdirSync(texdir)) {
    F.writeFile('/tex/' + name, fs.readFileSync(path.join(texdir, name)));
  }
  const opened = new Set(), missing = new Set();
  const originalOpen = F.open;
  F.open = function (name, ...args) {
    try {
      const stream = originalOpen.call(this, name, ...args);
      if (typeof name === 'string') opened.add(path.posix.resolve(F.cwd(), name));
      return stream;
    } catch (error) {
      if (typeof name === 'string') missing.add(path.posix.resolve(F.cwd(), name));
      throw error;
    }
  };
  let status, repeat, expected, inputCounts, carryAuxSha256;
  if (mode === 'format') {
    try { status = m._compileFormat(); }
    catch (error) {
      for (const name of F.readdir('/work')) {
        const target = '/work/' + name;
        if (F.isFile(F.stat(target).mode)) console.error('partial output', name, F.stat(target).size);
      }
      throw error;
    }
    expected = 'xelatex.fmt';
  } else {
    F.writeFile('/tex/xelatex.fmt', fs.readFileSync(mode === 'foreign-format'
      ? '/inputs/resources/files/pdflatex.fmt' : path.join(out, 'xelatex.fmt')));
    if (mode === 'missing-font') F.unlink('/tex/DejaVuSerif.ttf');
    const main = mode === 'emoji' ? 'розділ з пробілом/головний 🧪.tex' : 'розділ з пробілом/головний.tex';
    F.mkdir('/work/розділ з пробілом');
    F.mkdir('/work/parts');
    if (mode === 'emoji') {
      assert.equal(m.ccall('setMainEntry', 'number', ['string'], [main]), -2);
      const report = {mode, status: -2, reason: 'RetainedEngineMainFilenameNonBMPUnsupported'};
      fs.writeFileSync(path.join(out, mode + '.json'), JSON.stringify(report, null, 2) + '\n');
      console.log(JSON.stringify(report));
      return;
    }
    let source = String.raw`\documentclass{article}
\usepackage{fontspec}
\setmainfont{DejaVuSerif.ttf}[Path=/tex/,BoldFont=DejaVuSerif-Bold.ttf]
\begin{document}
\input{parts/текст.tex}
Reference: Section~\ref{sec:one}. \textbf{Іі Її Єє Ґґ.}
\end{document}`;
    if (mode === 'failure') source = '\\documentclass{article}\\begin{document}\\unknowncommand\\end{document}';
    if (mode === 'confinement') source = '\\documentclass{article}\\begin{document}\\input{/etc/passwd}\\end{document}';
    if (mode === 'shell') source = '\\documentclass{article}\\begin{document}\\immediate\\write18{touch /work/escaped}Safe\\end{document}';
    if (mode === 'spin') source = String.raw`\documentclass{article}\begin{document}\loop\iftrue\repeat`;
    if (mode === 'stale-output') {
      source = String.raw`\documentclass{article}\begin{document}\end{document}`;
      F.writeFile('/work/головний.xdv', fs.readFileSync('/control/candidate/xetex-v1/acceptance.xdv'));
    }
    const capacityCase = ['capacity-pass1', 'capacity-pass2', 'capacity-max-state'].includes(mode);
    if (capacityCase) {
      const inputs = [];
      for (let n = 0; n < 62; ++n) {
        const name = 'parts/captured' + n + '.tex';
        F.writeFile('/work/' + name, '% Actual captured input ' + n + '\n');
        inputs.push('\\input{' + name + '}');
      }
      source = source.replace('\\end{document}', inputs.join('\n') + '\n\\end{document}');
    }
    F.writeFile('/work/' + main, source);
    F.writeFile('/work/parts/текст.tex', '\\section{Український текст}\\label{sec:one}\nПривіт, світе! Іі Її Єє Ґґ.\n');
    if (mode === 'pass2') F.writeFile('/work/головний.aux', fs.readFileSync(path.join(out, 'pass1-головний.aux')));
    if (mode === 'input-limit') F.writeFile('/work/oversized.bin', new Uint8Array(8388609));
    if (capacityCase && mode !== 'capacity-pass1') {
      const carry = fs.readFileSync(path.join(out, 'capacity-pass1-головний.aux'));
      carryAuxSha256 = digest(carry);
      F.writeFile('/work/головний.aux', carry);
      if (mode === 'capacity-max-state') {
        for (let n = 0; n < 127; ++n) F.writeFile('/work/state' + n + '.aux', '\\relax\n');
      }
    }
    if (mode === 'count-limit') for (let n = 0; n < 191; ++n) F.writeFile('/work/extra' + n, 'x');
    if (capacityCase || mode === 'count-limit') {
      const count = directory => F.readdir(directory).filter(n => n !== '.' && n !== '..').reduce((total, name) => {
        const entry = directory + '/' + name;
        return total + (F.isDir(F.stat(entry).mode) ? count(entry) : 1);
      }, 0);
      const state = mode === 'capacity-max-state' ? 128 : mode === 'capacity-pass2' ? 1 : 0;
      inputCounts = capacityCase ? {captured: 64, state, combined: count('/work')}
        : {combined: count('/work')};
      assert.equal(inputCounts.combined, capacityCase ? 64 + state : 193);
    }
    assert.equal(m.ccall('setMainEntry', 'number', ['string'], [main]), 0);
    for (const invalid of ['../secret.tex', 'x/../../secret.tex', '/etc/passwd', 'x"\\input{secret}', 'x|foo', '/tex/latex.ltx']) {
      assert.equal(m.ccall('setMainEntry', 'number', ['string'], [invalid]), -2, invalid);
    }
    assert.equal(m.ccall('setMainEntry', 'number', ['string'], [main]), 0);
    if (mode === 'spin') require('node:worker_threads').parentPort.postMessage({compilationStarted: true});
    status = m._compileLaTeX();
    expected = mode === 'emoji' ? 'головний 🧪.xdv' : 'головний.xdv';
    if (mode === 'memory-limit') assert.equal(m._malloc(600 * 1024 * 1024), 0, 'allocation beyond the 512 MiB maximum must fail');
    if (mode === 'write-limits' && status === 0) {
      assert.throws(() => F.writeFile('/tex/overwrite.tex', 'forbidden'));
      assert.throws(() => F.writeFile('/work/too-large.xdv', new Uint8Array(33554433)));
      const accepted = new Uint8Array(33554432);
      F.writeFile('/work/limit1.bin', accepted);
      F.writeFile('/work/limit2.bin', accepted);
      assert.throws(() => F.writeFile('/work/limit3.bin', accepted), '96 MiB aggregate growth must reject');
      for (const name of ['limit1.bin', 'limit2.bin', 'limit3.bin', 'too-large.xdv']) {
        try { F.unlink('/work/' + name); } catch {}
      }
    }
  }
  repeat = mode === 'format' ? m._compileFormat() : m._compileLaTeX();
  assert.equal(repeat, -10, 'repeat attempt must require a fresh module');
  const files = {};
  for (const name of F.readdir('/work')) {
    const target = '/work/' + name;
    if (!F.isFile(F.stat(target).mode)) continue;
    const data = F.readFile(target);
    files[name] = {length: data.length, sha256: digest(data)};
    fs.writeFileSync(path.join(out, mode + '-' + name), data);
    if (mode === 'format' && name === 'xelatex.fmt' && status === 0) fs.writeFileSync(path.join(out, name), data);
    if (name.endsWith('.xdv')) {
      assert.equal(data[0], 247); assert.equal(data[1], 7, 'genuine XDV version 7');
    }
  }
  fs.writeFileSync(path.join(out, mode + '.log'), log);
  const report = {mode, status, repeat, expected, files, truncated, inputCounts, carryAuxSha256,
    opened: [...opened].sort(), missing: [...missing].sort()};
  fs.writeFileSync(path.join(out, mode + '.json'), JSON.stringify(report, null, 2) + '\n');
  console.log(JSON.stringify({mode, status, repeat, files, truncated, inputCounts, carryAuxSha256}));
  if (['failure', 'confinement', 'input-limit', 'count-limit', 'foreign-format', 'missing-font', 'stale-output'].includes(mode)) {
    assert.notEqual(status, 0); assert.ok(!files[expected], 'failed fresh module must not expose a prior artifact');
  } else {
    assert.equal(status, 0); assert.ok(files[expected], 'expected actual output');
  }
  if (mode === 'input-limit' || mode === 'count-limit') assert.equal(status, -4);
  if (mode === 'stale-output') assert.equal(status, -5);
  if (['pass2', 'capacity-pass2', 'capacity-max-state'].includes(mode)) {
    assert.ok(!log.includes('undefined references'), 'cross-reference must resolve');
  }
  if (inputCounts && inputCounts.captured === 64) {
    for (let n = 0; n < 62; ++n) assert.ok(opened.has('/work/parts/captured' + n + '.tex'), 'captured source must actually be read');
    if (inputCounts.state) assert.ok(opened.has('/work/головний.aux'), 'prior AUX must actually be read');
  }
  if (mode === 'shell') assert.ok(!files.escaped, 'shell escape must not run');
})().catch(error => { console.error(error); process.exitCode = 1; });
