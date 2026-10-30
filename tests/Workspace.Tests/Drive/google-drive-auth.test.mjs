// Optional dependency-free Node tests for the browser glue, using fake GIS/Picker.
// These do not establish real OAuth, account permissions, CSP or deployed CORS.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
const source = await readFile(
    new URL(
        "../../../src/Client/wwwroot/js/google-drive-auth.js",
        import.meta.url,
    ),
    "utf8",
);
let instance = 0;
async function setup({ holdScripts = false, holdPickerLoad = false } = {}) {
    const scripts = [],
        elements = [],
        moduleLoads = [];
    const timers = new Map(),
        timerDelays = new Map();
    let nextTimer = 0,
        consent,
        requested = 0,
        picked;
    globalThis.setTimeout = (fn, delay) => {
        const id = ++nextTimer;
        timerDelays.set(id, delay);
        timers.set(id, fn);
        return id;
    };
    globalThis.clearTimeout = (id) => timers.delete(id);
    Object.defineProperty(globalThis, "navigator", {
        configurable: true,
        value: { onLine: true },
    });
    globalThis.window = { location: { origin: "https://app.example.invalid" } };
    globalThis.document = {
        createElement: () => ({ remove() {} }),
        head: {
            appendChild(element) {
                scripts.push(element.src);
                elements.push(element);
                if (!holdScripts) queueMicrotask(() => element.onload?.());
            },
        },
    };
    // Any attempt to add REST, persistence, or logging to glue fails these tests.
    globalThis.fetch = () => {
        throw new Error("REST belongs in C#");
    };
    globalThis.localStorage = {
        setItem() {
            throw new Error("persistence forbidden");
        },
    };
    globalThis.sessionStorage = globalThis.localStorage;
    globalThis.indexedDB = {
        open() {
            throw new Error("persistence forbidden");
        },
    };
    const pickerState = {
        token: null,
        disposed: false,
        appId: null,
        key: null,
        origin: null,
        visible: false,
        duringBuild: null,
        builds: 0,
    };
    class DocsView {
        setIncludeFolders() {
            return this;
        }
        setSelectFolderEnabled() {
            return this;
        }
        setMimeTypes(value) {
            assert.equal(value, "application/zip,application/x-zip-compressed");
            return this;
        }
    }
    class Builder {
        setDeveloperKey(key) {
            pickerState.key = key;
            return this;
        }
        setAppId(id) {
            pickerState.appId = id;
            return this;
        }
        setOAuthToken(token) {
            pickerState.token = token;
            return this;
        }
        setOrigin(origin) {
            pickerState.origin = origin;
            return this;
        }
        addView() {
            return this;
        }
        setCallback(callback) {
            picked = callback;
            return this;
        }
        build() {
            pickerState.builds++;
            pickerState.duringBuild?.();
            return {
                setVisible(value) {
                    pickerState.visible = value;
                },
                dispose() {
                    pickerState.disposed = true;
                    pickerState.token = null;
                },
            };
        }
    }
    globalThis.google = {
        accounts: {
            oauth2: {
                initTokenClient(options) {
                    consent = options;
                    return {
                        requestAccessToken(options) {
                            requested++;
                            assert.deepEqual(options, { prompt: "consent" });
                        },
                    };
                },
            },
        },
        picker: {
            DocsView,
            PickerBuilder: Builder,
            ViewId: { DOCS: "docs" },
            Action: { CANCEL: "cancel", PICKED: "picked" },
        },
    };
    globalThis.gapi = {
        load(name, options) {
            assert.equal(name, "picker");
            moduleLoads.push(options);
            if (!holdPickerLoad) options.callback();
        },
    };
    const glue = await import(
        `data:text/javascript;base64,${Buffer.from(source).toString("base64")}#${++instance}`
    );
    async function until(predicate) {
        for (let i = 0; i < 30; i++) {
            if (predicate()) return;
            await Promise.resolve();
        }
        assert.fail("finite mock did not reach callback");
    }
    return {
        glue,
        scripts,
        timers,
        timerDelays,
        elements,
        moduleLoads,
        pickerState,
        until,
        get consent() {
            return consent;
        },
        get requested() {
            return requested;
        },
        get picked() {
            return picked;
        },
    };
}

test("no startup side effects; explicit exact drive.file consent, bounded reply", async () => {
    const env = await setup();
    assert.deepEqual(env.scripts, []);
    assert.equal(env.requested, 0);
    const connecting = env.glue.connect("synthetic.apps.googleusercontent.com");
    await env.until(() => env.consent);
    assert.equal(
        env.consent.scope,
        "https://www.googleapis.com/auth/drive.file",
    );
    assert.equal(env.consent.include_granted_scopes, false);
    assert.equal(env.consent.client_secret, undefined);
    env.consent.callback({
        access_token: "synthetic-token",
        scope: env.consent.scope,
        expires_in: 3600,
    });
    assert.deepEqual(await connecting, {
        code: "ready",
        token: "synthetic-token",
        scope: env.consent.scope,
        expiresIn: 3600,
    });
    assert.deepEqual(env.scripts, ["https://accounts.google.com/gsi/client"]);
    assert.equal(env.timers.size, 0);
});

test("denied/offline/popup are finite safe results and never auto reconnect", async () => {
    const env = await setup();
    navigator.onLine = false;
    assert.deepEqual(
        await env.glue.connect("synthetic.apps.googleusercontent.com"),
        { code: "offline" },
    );
    assert.deepEqual(env.scripts, []);
    navigator.onLine = true;
    const denied = env.glue.connect("synthetic.apps.googleusercontent.com");
    await env.until(() => env.consent);
    env.consent.callback({
        error: "access_denied",
        error_description: "must not surface raw",
    });
    assert.deepEqual(await denied, { code: "denied" });
    assert.equal(env.requested, 1);
    const popup = env.glue.connect("synthetic.apps.googleusercontent.com");
    await env.until(() => env.requested === 2);
    env.consent.error_callback({ type: "popup_closed" });
    assert.deepEqual(await popup, { code: "popup" });
    assert.equal(env.requested, 2);
});

test("disconnect resolves pending consent and ignores late token", async () => {
    const env = await setup();
    const pending = env.glue.connect("synthetic.apps.googleusercontent.com");
    await env.until(() => env.consent);
    env.glue.disconnect();
    assert.deepEqual(await pending, { code: "cancelled" });
    env.consent.callback({
        access_token: "late-token",
        scope: env.consent.scope,
        expires_in: 3600,
    });
    assert.equal(env.timers.size, 0);
});

test("Picker uses public app setup, only returns selected bounded ZIP data, disconnect disposes", async () => {
    const env = await setup();
    const choosing = env.glue.chooseZip(
        "synthetic-key",
        "123",
        "synthetic-token",
    );
    await env.until(() => env.picked);
    assert.equal(env.pickerState.key, "synthetic-key");
    assert.equal(env.pickerState.appId, "123");
    assert.equal(env.pickerState.origin, "https://app.example.invalid");
    env.picked({
        action: "picked",
        docs: [
            {
                id: "safe_id",
                name: "Україна🙂.zip",
                url: "ignored",
                extra: "ignored",
            },
        ],
    });
    assert.deepEqual(await choosing, {
        code: "ready",
        id: "safe_id",
        name: "Україна🙂.zip",
    });
    assert.equal(env.pickerState.disposed, true);
    assert.equal(env.pickerState.token, null);
    assert.deepEqual(env.scripts, ["https://apis.google.com/js/api.js"]);
    const again = env.glue.chooseZip("synthetic-key", "123", "synthetic-token");
    const previous = env.picked;
    await env.until(() => env.picked !== previous);
    env.glue.disconnect();
    assert.deepEqual(await again, { code: "cancelled" });
    assert.equal(env.pickerState.token, null);
    assert.equal(env.timers.size, 0);
    env.picked({ action: "picked", docs: [{ id: "late", name: "late.zip" }] });
    assert.equal(env.pickerState.visible, false);
});

test("malformed OAuth and Picker callbacks return safe bounded failures", async () => {
    const env = await setup();
    for (const reply of [null, {}, { expires_in: Symbol("malformed") }]) {
        const count = env.requested;
        const pending = env.glue.connect(
            "synthetic.apps.googleusercontent.com",
        );
        await env.until(() => env.requested > count);
        env.consent.callback(reply);
        assert.deepEqual(await pending, { code: "configuration" });
    }
    for (const reply of [null, { action: "picked", docs: [null] }]) {
        const previous = env.picked;
        const pending = env.glue.chooseZip(
            "synthetic-key",
            "123",
            "synthetic-token",
        );
        await env.until(() => env.picked && env.picked !== previous);
        env.picked(reply);
        assert.deepEqual(await pending, { code: "configuration" });
        assert.equal(env.pickerState.token, null);
    }
    assert.equal(env.timers.size, 0);
});

test("cancellation during Picker build disposes its late object without showing it", async () => {
    const env = await setup();
    env.pickerState.duringBuild = () => env.glue.disconnect();
    const pending = env.glue.chooseZip(
        "synthetic-key",
        "123",
        "synthetic-token",
    );
    assert.deepEqual(await pending, { code: "cancelled" });
    assert.equal(env.pickerState.visible, false);
    assert.equal(env.pickerState.disposed, true);
    assert.equal(env.pickerState.token, null);
    assert.equal(env.timers.size, 0);
});

for (const cached of [false, true]) {
    test(`offline after ${cached ? "cached" : "held"} GIS await prevents consent`, async () => {
        const env = await setup({ holdScripts: !cached });
        if (cached) {
            const warm = env.glue.connect(
                "synthetic.apps.googleusercontent.com",
            );
            await env.until(() => env.consent);
            env.consent.error_callback();
            await warm;
        }
        const count = env.requested;
        const pending = env.glue.connect(
            "synthetic.apps.googleusercontent.com",
        );
        navigator.onLine = false;
        if (!cached) env.elements[0].onload();
        let reply;
        pending.then((value) => {
            reply = value;
        });
        await env.until(() => reply);
        assert.deepEqual(reply, { code: "offline" });
        assert.equal(env.requested, count);
        assert.equal(env.timers.size, 0);
    });
}

for (const boundary of ["script", "module", "cached-module"]) {
    test(`offline at Picker ${boundary} boundary prevents next Google action`, async () => {
        const env = await setup({
            holdScripts: boundary === "script",
            holdPickerLoad: true,
        });
        if (boundary === "cached-module") {
            const warm = env.glue.chooseZip(
                "synthetic-key",
                "123",
                "synthetic-token",
            );
            await env.until(() => env.moduleLoads.length === 1);
            env.moduleLoads[0].callback();
            await env.until(() => env.picked);
            env.picked({ action: "cancel" });
            await warm;
        }
        const builds = env.pickerState.builds;
        const modules = env.moduleLoads.length;
        const pending = env.glue.chooseZip(
            "synthetic-key",
            "123",
            "synthetic-token",
        );
        if (boundary !== "script")
            await env.until(() => env.moduleLoads.length > modules);
        navigator.onLine = false;
        if (boundary === "script") env.elements[0].onload();
        else env.moduleLoads.at(-1).callback();
        let reply;
        pending.then((value) => {
            reply = value;
        });
        await env.until(() => reply);
        assert.deepEqual(reply, { code: "offline" });
        assert.equal(
            env.moduleLoads.length,
            modules + (boundary === "script" ? 0 : 1),
        );
        assert.equal(env.pickerState.builds, builds);
        assert.equal(env.pickerState.token, null);
    });
}

for (const operation of ["connect", "chooseZip"]) {
    for (const settles of ["load", "error", "timeout"]) {
        test(`disconnect settles ${operation} immediately but retains held script slot until ${settles}`, async () => {
            const env = await setup({ holdScripts: true });
            const invoke = () =>
                operation === "connect"
                    ? env.glue.connect("synthetic.apps.googleusercontent.com")
                    : env.glue.chooseZip(
                          "synthetic-key",
                          "123",
                          "synthetic-token",
                      );
            let reply;
            invoke().then((value) => {
                reply = value;
            });
            assert.equal(env.elements.length, 1);
            const element = env.elements[0],
                lateLoad = element.onload;
            env.glue.disconnect();
            await env.until(() => reply);
            assert.deepEqual(reply, { code: "cancelled" });
            assert.deepEqual(await invoke(), { code: "busy" });
            assert.equal(env.elements.length, 1);
            assert.equal(env.requested, 0);
            assert.equal(env.moduleLoads.length, 0);
            assert.equal(env.timers.size, 1); // unresolved loader still owns its deadline
            assert.equal(env.timerDelays.get([...env.timers.keys()][0]), 20000);
            if (settles === "load") element.onload();
            else if (settles === "error") element.onerror();
            else [...env.timers.values()][0]();
            await env.until(() => env.timers.size === 0);
            // Drain bounded microtasks, including failed-loader cleanup/finally.
            for (let i = 0; i < 10; i++) await Promise.resolve();
            const next = invoke();
            if (settles !== "load") env.elements.at(-1).onload();
            if (operation === "connect") {
                await env.until(() => env.requested === 1);
                lateLoad(); // old script completion must not affect the new consent
                env.consent.callback({
                    access_token: "new-token",
                    scope: env.consent.scope,
                    expires_in: 3600,
                });
                assert.equal((await next).token, "new-token");
            } else {
                await env.until(() => env.picked);
                lateLoad();
                assert.equal(env.pickerState.visible, true);
                env.picked({
                    action: "picked",
                    docs: [{ id: "new_id", name: "new.zip" }],
                });
                assert.equal((await next).id, "new_id");
            }
            assert.equal(env.timers.size, 0);
        });
    }
}

test("disconnect during held gapi module resolves public attempt; late callback cannot dispose next Picker", async () => {
    const env = await setup({ holdPickerLoad: true });
    let reply;
    env.glue.chooseZip("synthetic-key", "123", "old-token").then((value) => {
        reply = value;
    });
    await env.until(() => env.moduleLoads.length === 1);
    const old = env.moduleLoads[0];
    env.glue.disconnect();
    await env.until(() => reply);
    assert.deepEqual(reply, { code: "cancelled" });
    assert.deepEqual(
        await env.glue.chooseZip("synthetic-key", "123", "new-token"),
        { code: "busy" },
    );
    old.callback();
    for (let i = 0; i < 10; i++) await Promise.resolve();
    assert.equal(env.pickerState.builds, 0);
    const next = env.glue.chooseZip("synthetic-key", "123", "new-token");
    await env.until(() => env.moduleLoads.length === 2);
    env.moduleLoads[1].callback();
    await env.until(() => env.picked);
    old.callback();
    old.onerror();
    assert.equal(env.pickerState.token, "new-token");
    assert.equal(env.pickerState.disposed, false);
    assert.equal(env.pickerState.visible, true);
    env.picked({ action: "cancel" });
    assert.deepEqual(await next, { code: "cancelled" });
});

test("offline while consent is open cannot return a late token", async () => {
    const env = await setup();
    const pending = env.glue.connect("synthetic.apps.googleusercontent.com");
    await env.until(() => env.consent);
    navigator.onLine = false;
    env.consent.callback({
        access_token: "late-token",
        scope: env.consent.scope,
        expires_in: 3600,
    });
    assert.deepEqual(await pending, { code: "offline" });
    assert.equal(env.timers.size, 0);
});

for (const failure of ["onerror", "ontimeout", "owned-deadline"]) {
    test(`held gapi ${failure} releases only its loader and ignores late callbacks`, async () => {
        const env = await setup({ holdPickerLoad: true });
        const pending = env.glue.chooseZip("synthetic-key", "123", "old-token");
        await env.until(() => env.moduleLoads.length === 1);
        const old = env.moduleLoads[0];
        assert.equal(old.timeout, 20000);
        assert.equal(env.timers.size, 1);
        if (failure === "owned-deadline") {
            const [id, expire] = [...env.timers][0];
            assert.equal(env.timerDelays.get(id), 20000);
            expire();
        } else old[failure]();
        assert.deepEqual(await pending, { code: "configuration" });
        assert.equal(env.pickerState.builds, 0);
        const next = env.glue.chooseZip("synthetic-key", "123", "new-token");
        await env.until(() => env.moduleLoads.length === 2);
        old.callback();
        assert.equal(env.pickerState.builds, 0);
        env.moduleLoads[1].callback();
        await env.until(() => env.picked);
        env.picked({ action: "cancel" });
        assert.deepEqual(await next, { code: "cancelled" });
        assert.equal(env.timers.size, 0);
    });
}

for (const operation of ["connect", "chooseZip"]) {
    test(`cached ${operation} await cannot cross disconnect generation or run offline`, async () => {
        const env = await setup();
        const invoke = () =>
            operation === "connect"
                ? env.glue.connect("synthetic.apps.googleusercontent.com")
                : env.glue.chooseZip("synthetic-key", "123", "synthetic-token");
        const warm = invoke();
        await env.until(() =>
            operation === "connect" ? env.consent : env.picked,
        );
        if (operation === "connect") env.consent.error_callback();
        else env.picked({ action: "cancel" });
        await warm;
        const requested = env.requested,
            modules = env.moduleLoads.length,
            builds = env.pickerState.builds;
        const pending = invoke();
        env.glue.disconnect();
        assert.deepEqual(await pending, { code: "cancelled" });
        assert.equal(env.requested, requested);
        assert.equal(env.moduleLoads.length, modules);
        assert.equal(env.pickerState.builds, builds);
        navigator.onLine = false;
        assert.deepEqual(await invoke(), { code: "offline" });
        assert.equal(env.scripts.length, 1);
        assert.equal(env.requested, requested);
        assert.equal(env.moduleLoads.length, modules);
        assert.equal(env.pickerState.builds, builds);
        assert.equal(env.timers.size, 0);
    });
}

test("late consent denial/token/popup callbacks cannot finish a new consent attempt", async () => {
    const env = await setup();
    const first = env.glue.connect("synthetic.apps.googleusercontent.com");
    await env.until(() => env.consent);
    const old = env.consent;
    old.error_callback();
    assert.deepEqual(await first, { code: "popup" });
    const next = env.glue.connect("synthetic.apps.googleusercontent.com");
    await env.until(() => env.requested === 2);
    old.error_callback();
    old.callback({ error: "access_denied" });
    old.callback({
        access_token: "old-token",
        scope: old.scope,
        expires_in: 3600,
    });
    assert.equal(env.timers.size, 1);
    env.consent.callback({
        access_token: "new-token",
        scope: env.consent.scope,
        expires_in: 3600,
    });
    assert.equal((await next).token, "new-token");
    assert.equal(env.timers.size, 0);
});

test("late Picker callback cannot dispose a new Picker; offline selection is refused", async () => {
    const env = await setup();
    const first = env.glue.chooseZip("synthetic-key", "123", "old-token");
    await env.until(() => env.picked);
    const old = env.picked;
    old({ action: "cancel" });
    await first;
    env.pickerState.disposed = false;
    const next = env.glue.chooseZip("synthetic-key", "123", "new-token");
    await env.until(() => env.picked !== old);
    old({ action: "picked", docs: [{ id: "old_id", name: "old.zip" }] });
    assert.equal(env.pickerState.visible, true);
    assert.equal(env.pickerState.disposed, false);
    assert.equal(env.pickerState.token, "new-token");
    navigator.onLine = false;
    env.picked({ action: "picked", docs: [{ id: "new_id", name: "new.zip" }] });
    assert.deepEqual(await next, { code: "offline" });
    assert.equal(env.pickerState.disposed, true);
    assert.equal(env.pickerState.token, null);
    assert.equal(env.timers.size, 0);
});

test("consent and Picker retain their 60/120 second public deadlines with late results ignored", async () => {
    const env = await setup();
    const consent = env.glue.connect("synthetic.apps.googleusercontent.com");
    await env.until(() => env.consent);
    let [id, expire] = [...env.timers][0];
    assert.equal(env.timerDelays.get(id), 60000);
    expire();
    assert.deepEqual(await consent, { code: "popup" });
    env.consent.callback({
        access_token: "late-token",
        scope: env.consent.scope,
        expires_in: 3600,
    });
    const picker = env.glue.chooseZip(
        "synthetic-key",
        "123",
        "synthetic-token",
    );
    await env.until(() => env.picked);
    [id, expire] = [...env.timers][0];
    assert.equal(env.timerDelays.get(id), 120000);
    expire();
    assert.deepEqual(await picker, { code: "cancelled" });
    env.picked({
        action: "picked",
        docs: [{ id: "late_id", name: "late.zip" }],
    });
    assert.equal(env.pickerState.visible, false);
    assert.equal(env.pickerState.token, null);
    assert.equal(env.timers.size, 0);
});
