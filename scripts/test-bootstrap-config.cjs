// Finite bootstrap contract test; no browser, runtime downloads or SW replacement.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const html = fs.readFileSync(
  path.join(__dirname, "../src/Client/wwwroot/index.html"),
  "utf8",
);
const loader = [
  ...html.matchAll(
    /<script\b([^>]*)src="_framework\/blazor\.webassembly\.js\?v=v9"([^>]*)><\/script>/g,
  ),
];
assert.equal(loader.length, 1);
assert.match(loader[0][0], /autostart="false"/);
const scripts = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)];
assert.equal(scripts.length, 1);
const flush = async () => {
  for (let i = 0; i < 4; i++) await Promise.resolve();
};
function setup(swSupported) {
  let resolve,
    reject,
    starts = 0,
    configs = 0;
  const startup = new Promise((a, b) => {
    resolve = a;
    reject = b;
  });
  const listeners = [],
    messages = [],
    swListeners = [];
  const builder = {
    withConfig(value) {
      configs++;
      assert.deepEqual(Object.keys(value), ["maxParallelDownloads"]);
      assert.equal(value.maxParallelDownloads, 8);
      return this;
    },
  };
  const window = {
    addEventListener(name, callback) {
      listeners.push({ name, callback });
    },
  };
  const document = { readyState: "loading" };
  const sw = {
    controller: {
      postMessage(value) {
        messages.push(value);
      },
    },
    addEventListener(name, callback) {
      swListeners.push({ name, callback });
    },
    register(url) {
      assert.equal(url, "/service-worker.js");
      return new Promise(() => {});
    },
  };
  const context = vm.createContext({
    window,
    navigator: swSupported ? { serviceWorker: sw } : {},
    document,
    console,
    URL,
    location: {
      origin: "https://example.invalid",
      href: "https://example.invalid/",
    },
    performance: {
      getEntriesByType() {
        return [
          { name: "https://example.invalid/_framework/runtime.wasm" },
          { name: "https://example.invalid/_framework/blazor.webassembly.js?v=v9" },
          { name: "https://example.invalid/_framework/dotnet.js?v=v9" },
          { name: "https://example.invalid/other.js?private=not-cached" },
        ];
      },
    },
    Blazor: {
      start(options) {
        starts++;
        assert.equal(options.configureRuntime(builder), builder);
        assert.equal(options.loadBootResource("dotnetjs", "dotnet.js", "_framework/dotnet.js"), "_framework/dotnet.js?v=v9");
        assert.equal(options.loadBootResource("assembly", "Client.wasm", "_framework/Client.wasm"), undefined);
        return startup;
      },
    },
  });
  vm.runInContext(scripts[0][1], context, { timeout: 1000 });
  assert.equal(starts, 1);
  assert.equal(configs, 1);
  return {
    window,
    messages,
    resolve,
    reject,
    appStarted: vm.runInContext("appStarted", context, { timeout: 1000 }),
    activate() {
      for (const l of swListeners.filter((l) => l.name === "message"))
        l.callback({ data: { type: "activated" } });
    },
    result(ready) {
      for (const l of swListeners.filter((l) => l.name === "message"))
        l.callback({ data: { type: "warm-result", ready } });
    },
    load() {
      document.readyState = "complete";
      for (const l of listeners.filter((l) => l.name === "load")) l.callback();
    },
  };
}
async function run() {
  const noSw = setup(false);
  noSw.resolve();
  await noSw.appStarted;
  for (const order of ["load-first", "start-first"]) {
    const h = setup(true);
    h.activate();
    h.result(true);
    assert.equal(
      h.window.__oopOfflineReady,
      false,
      "early warm-result cannot claim successful bootstrap",
    );
    if (order === "load-first") h.load();
    else h.resolve();
    await flush();
    assert.equal(
      h.messages.length,
      0,
      "no warm until BOTH load and successful start",
    );
    if (order === "load-first") h.resolve();
    else h.load();
    await flush();
    assert.equal(h.messages.length, 1, "warm after load AND startup");
    const warm = h.messages[0];
    assert.equal(warm.type, "warm");
    assert.ok(warm.urls.includes("/_framework/runtime.wasm"));
    assert.ok(warm.urls.includes("/_framework/blazor.webassembly.js?v=v9"));
    assert.ok(warm.urls.includes("/_framework/dotnet.js?v=v9"));
    assert.ok(!warm.urls.some(u => u.includes("private=")));
    assert.ok(warm.urls.includes("/js/compile-history-index.js"));
    assert.ok(warm.urls.includes("/js/compile-artifacts-storage.js"));
    assert.ok(warm.urls.includes("/js/google-drive-auth.js"));
    assert.ok(
      !warm.urls.some(
        (u) => u.includes("private-inputs") || u.endsWith(".pdf"),
      ),
      "no PDF/native-input provisioning",
    );
    h.result(false);
    assert.equal(h.window.__oopOfflineReady, false);
    h.result(true);
    assert.equal(h.window.__oopOfflineReady, true);
    assert.equal(typeof h.window.__oopUpdateGuardCanActivate, "function");
    assert.equal(
      (await h.window.__oopUpdateGuardCanActivate()).safe,
      false,
      "update guard still fails closed before registration",
    );
  }
  const failed = setup(true);
  const rejected = assert.rejects(failed.appStarted, /startup failure/);
  failed.reject(new Error("startup failure"));
  await rejected;
  failed.result(true);
  assert.equal(
    failed.window.__oopOfflineReady,
    false,
    "failed startup cannot claim readiness",
  );
  assert.equal(failed.messages.length, 0);
  console.log(
    "Bootstrap contract PASS: start once/withConfig8; no-SW startup; held start/load barriers both orders; failed startup not ready; exact history glue warmed.",
  );
}
let timer;
Promise.race([
  run(),
  new Promise((_, reject) => {
    timer = setTimeout(
      () => reject(new Error("finite bootstrap test deadline")),
      2000,
    );
  }),
])
  .catch((e) => {
    console.error(e);
    process.exitCode = 1;
  })
  .finally(() => clearTimeout(timer));
