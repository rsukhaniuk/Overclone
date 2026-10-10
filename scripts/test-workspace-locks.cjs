// Actual browser Web Locks glue only; NOT browser batch-application coverage.
const assert = require("node:assert/strict");
const http = require("node:http");
const { chromium } = require("playwright");
const upstream = process.env.APP_URL || "http://app:8080";
const base = "http://127.0.0.1:8098";
const proxy = http.createServer((req, res) => {
 const url = new URL(req.url, upstream);
 const r = http.request(
  url,
  { headers: { ...req.headers, host: url.host } },
  (v) => {
   res.writeHead(v.statusCode, v.headers);
   v.pipe(res);
  },
 );
 r.on("error", () => res.destroy());
 req.pipe(r);
});
(async () => {
 await new Promise((r) => proxy.listen(8098, "127.0.0.1", r));
 const browser = await chromium.launch({ headless: true });
 try {
  const context = await browser.newContext({ serviceWorkers: "block" });
  const a = await context.newPage(),
   b = await context.newPage();
  const errors = [];
  a.on("pageerror", (e) => errors.push(String(e)));
  b.on("pageerror", (e) => errors.push(String(e)));
  await a.goto(base);
  await b.goto(base);
  const acquire = (p) =>
   p.evaluate(async () => {
    const m = await import("./js/workspace-storage.js");
    return m.tryAcquireLock("ownership-test");
   });
  const owns = (p, id) =>
   p.evaluate(async (id) => {
    const m = await import("./js/workspace-storage.js");
    return m.ownsLock(id, "ownership-test");
   }, id);
  const release = (p, id) =>
   p.evaluate(
    async (id) => (await import("./js/workspace-storage.js")).releaseLock(id),
    id,
   );
  const first = await acquire(a);
  assert.ok(first);
  assert.equal(await owns(a, first), true);
  assert.equal(await owns(b, first), false);
  assert.equal(
   await b.evaluate(async () =>
    (await import("./js/workspace-storage.js")).lockHeld("ownership-test"),
   ),
   true,
  );
  assert.equal(await acquire(b), null);
  assert.equal(await release(a, first), true);
  const second = await acquire(b);
  assert.ok(second);
  assert.equal(await owns(b, second), true);
  assert.equal(await owns(a, second), false);
  await release(b, second);
  const stolen = await acquire(a);
  assert.ok(stolen);
  // Test-only external steal: the old browser request must invalidate its
  // local entry, even though the name remains held exclusively by another tab.
  await b.evaluate(
   () =>
    new Promise((resolve) => {
     let release;
     const hold = new Promise((r) => (release = r));
     navigator.locks.request(
      "ownership-test",
      { mode: "exclusive", steal: true },
      () => {
       window.__releaseLockTest = release;
       resolve(true);
       return hold;
      },
     );
    }),
  );
  assert.equal(await owns(a, stolen), false);
  assert.equal(await release(a, stolen), false);
  await b.evaluate(() => window.__releaseLockTest());
  const missing = await acquire(a);
  assert.ok(missing);
  assert.equal(
   await a.evaluate(async (id) => {
    const m = await import("./js/workspace-storage.js");
    const original = navigator.locks.query;
    try {
     navigator.locks.query = undefined;
     return await m.ownsLock(id, "ownership-test");
    } finally {
     navigator.locks.query = original;
    }
   }, missing),
   false,
  );
  await release(a, missing);
  assert.deepEqual(errors, []);
  process.stdout.write(
   "PASS Web Locks ownership: actual own ID, other tab, release, external steal cleanup, missing query fails closed (no batch UI claim)\n",
  );
 } finally {
  await browser.close();
  await new Promise((r) => proxy.close(r));
 }
})().catch((e) => {
 process.stderr.write((e?.stack || String(e)) + "\n");
 process.exitCode = 1;
});
