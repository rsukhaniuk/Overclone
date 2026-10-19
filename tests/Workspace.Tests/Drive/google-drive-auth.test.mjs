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
async function setup() {
    const scripts = [];
    const timers = new Map();
    let nextTimer = 0,
        consent,
        requested = 0,
        picked;
    globalThis.setTimeout = (fn) => {
        const id = ++nextTimer;
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
                queueMicrotask(() => element.onload?.());
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
            options.callback();
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
