// Dependency-free synthetic guard and resource lifetime contracts for the
// deployed Ollama acceptance runner.
const { test } = require("node:test");
const assert = require("node:assert/strict");
const { PassThrough } = require("node:stream");
const fs = require("node:fs");
const path = require("node:path");
const os = require("node:os");
const { spawnSync } = require("node:child_process");
const { createHash } = require("node:crypto");
const R = require("../../scripts/test-ai-deployed-ui.cjs");
const script = path.resolve(__dirname, "../../scripts/test-ai-deployed-ui.cjs");
const fixture = path.resolve(__dirname, "../fixtures/r3-live/main.tex");
const key = "synthetic-guard-credential";
const env = (overrides = {}) => ({
    APP_URL: "https://app.example.invalid",
    R3_MODEL: "explicit-model",
    R3_BROWSER_CHANNEL: "chrome",
    RESULT_DIR: path.join(os.tmpdir(), "r3-never-written-guard"),
    ...overrides,
});
const code = (expected) => (error) => {
    assert.ok(error instanceof R.Refusal);
    assert.equal(error.code, expected);
    assert.equal(error.message, expected);
    assert.ok(!String(error).includes(key));
    return true;
};
function hooks() {
    const calls = {
        network: 0,
        launch: 0,
        context: 0,
        page: 0,
        action: 0,
        contextClose: 0,
        browserClose: 0,
    };
    const page = {
        setDefaultTimeout(n) {
            assert.equal(n, 100000);
        },
        setDefaultNavigationTimeout(n) {
            assert.equal(n, 100000);
        },
    };
    const context = {
        async newPage() {
            calls.page++;
            return page;
        },
        async close() {
            calls.contextClose++;
        },
    };
    const browser = {
        async newContext(options) {
            calls.context++;
            assert.deepEqual(options, { serviceWorkers: "allow" });
            return context;
        },
        async close() {
            calls.browserClose++;
        },
    };
    return {
        calls,
        page,
        context,
        browser,
        async network() {
            calls.network++;
        },
        async launch(channel) {
            calls.launch++;
            assert.equal(channel, "chrome");
            return browser;
        },
        async action() {
            calls.action++;
            return { synthetic: true };
        },
    };
}
async function until(check) {
    for (let i = 0; i < 80; i++) {
        if (check()) return;
        await new Promise((resolve) => setImmediate(resolve));
    }
    assert.fail("finite synthetic boundary did not reach checkpoint");
}
test("fixture is exact approved LF/no-BOM Unicode byte recipe, not a compiler fixture", () => {
    const expected = Buffer.from(
        "\\documentclass{article}\n\\begin{document}\nSynthetic ALPHA. Україна🙂 \u0435\u0301\n\\end{document}\n",
    );
    const actual = fs.readFileSync(fixture);
    assert.deepEqual(actual, expected);
    assert.equal(actual.length, 97);
    assert.equal(
        createHash("sha256").update(actual).digest("hex"),
        "719163f50c023f5aeccc60d68ed8f29b670960d2a61b07bc91c8df0ee6ed414f",
    );
});
test("module import, help and default entry are side-effect free (no stdin consumption or hooks)", async () => {
    const h = hooks();
    const held = new PassThrough();
    let resumed = false;
    held.on("resume", () => {
        resumed = true;
    });
    for (const argv of [
        [],
        ["--help"],
        ["--api-key=" + key],
        ["--allow-live", key],
    ]) {
        let output = "";
        const result = await R.main({
            argv,
            env: env({ APP_URL: key }),
            stdin: held,
            stdout: {
                write(s) {
                    output += s;
                },
            },
            testHooks: h,
        });
        assert.equal(result, argv[0] === "--help" ? 0 : 1);
        assert.ok(!output.includes(key));
    }
    assert.equal(resumed, false);
    assert.equal(h.calls.network, 0);
    assert.equal(h.calls.launch, 0);
});
test("test-only launch/network hooks are forbidden with the live flag", async () => {
    const h = hooks();
    let output = "";
    assert.equal(
        await R.main({
            argv: ["--allow-live"],
            env: env(),
            stdout: {
                write(s) {
                    output += s;
                },
            },
            testHooks: h,
        }),
        1,
    );
    assert.deepEqual(JSON.parse(output), {
        passed: false,
        code: "test-hook-forbidden",
    });
    await assert.rejects(
        R._test.exercise({
            argv: ["--allow-live"],
            env: env(),
            hooks: h,
        }),
        code("test-hook-forbidden"),
    );
    assert.equal(h.calls.network, 0);
    assert.equal(h.calls.launch, 0);
});
for (const [label, overrides, expected = "configuration"] of [
    ["missing origin", { APP_URL: undefined }],
    ["http origin", { APP_URL: "http://app.example.invalid" }],
    ["URL path", { APP_URL: "https://app.example.invalid/workspace" }],
    ["trailing slash", { APP_URL: "https://app.example.invalid/" }],
    ["query key", { APP_URL: "https://app.example.invalid?apiKey=" + key }],
    ["fragment", { APP_URL: "https://app.example.invalid#" + key }],
    ["URL userinfo", { APP_URL: "https://" + key + "@app.example.invalid" }],
    ["noncanonical origin", { APP_URL: "https://APP.example.invalid" }],
    ["retired provider selector", { R3_PROVIDER: "Ollama" }],
    ["missing model", { R3_MODEL: "" }],
    ["model URL", { R3_MODEL: "https://bad.invalid/model" }],
    ["absolute model path", { R3_MODEL: "/models/model" }],
    ["model overflow", { R3_MODEL: "m".repeat(129) }],
    ["model whitespace", { R3_MODEL: "m m" }],
    ["unknown R3 config", { R3_EXTRA: "ignored?" }],
    ["credential file source", { R3_SECRET_INPUT: "/private/key" }],
    [
        "Playwright API debug logging",
        { DEBUG: "pw:api" },
        "unsafe-instrumentation",
    ],
    ["Playwright inspector", { PWDEBUG: "1" }, "unsafe-instrumentation"],
    [
        "Node protocol debug logging",
        { NODE_DEBUG: "http,net" },
        "unsafe-instrumentation",
    ],
    [
        "native protocol debug logging",
        { NODE_DEBUG_NATIVE: "http" },
        "unsafe-instrumentation",
    ],
    [
        "inherited Node instrumentation",
        { NODE_OPTIONS: "--inspect" },
        "unsafe-instrumentation",
    ],
    ["key environment source", { GEMINI_API_KEY: key }, "credential-source"],
    [
        "access token environment source",
        { ACCESS_TOKEN: key },
        "credential-source",
    ],
    ["missing channel", { R3_BROWSER_CHANNEL: undefined }],
    ["unknown channel", { R3_BROWSER_CHANNEL: "chromium-default" }],
    ["relative output", { RESULT_DIR: "result" }],
    ["source-tree output", { RESULT_DIR: path.dirname(script) }],
    ["public service override", { R3_OLLAMA_URL: "https://service.example.invalid" }],
]) {
    test(`guard rejects ${label} before injected network/launch/write`, async () => {
        const h = hooks();
        const settings = env(overrides);
        await assert.rejects(
            R._test.exercise({ env: settings, hooks: h }),
            code(expected),
        );
        assert.equal(h.calls.network, 0);
        assert.equal(h.calls.launch, 0);
        assert.equal(h.calls.action, 0);
    });
}
for (const local of [
    "http://localhost:11434",
    "http://127.0.0.1:11434",
]) {
    test(`local endpoint syntax accepts explicit local class ${new URL(local).hostname}`, () => {
        assert.equal(
            R.configuration(
                env({
                    R3_MODEL: "org/model:tag",
                    R3_OLLAMA_URL: local,
                }),
            ).endpoint,
            local + "/api/chat",
        );
    });
}
for (const local of [
    "https://service.example.invalid",
    "https://127.0.0.1:11434",
    "http://127.0.0.1:11435",
    "http://[::1]:11434",
    "http://10.1.2.3:11434",
    "http://8.8.8.8",
    "http://172.32.0.1",
    "http://localhost:11434/",
    "http://user:pass@localhost",
    "file:///key",
    "http://localhost?key=x",
    "http://localhost/path",
]) {
    test("local endpoint boundary refuses public/noncanonical/credential/path inputs", () => {
        assert.throws(
            () =>
                R.configuration(
                    env({ R3_OLLAMA_URL: local }),
                ),
            code("configuration"),
        );
    });
}
test("synthetic boundary success uses exact channel/SW allowed and closes once, never writes a live receipt", async () => {
    const h = hooks();
    assert.deepEqual(
        await R._test.exercise({ env: env(), hooks: h }),
        { synthetic: true },
    );
    assert.deepEqual(h.calls, {
        network: 1,
        launch: 1,
        context: 1,
        page: 1,
        action: 1,
        contextClose: 1,
        browserClose: 1,
    });
});
test("all three selected browser channels are passed unchanged to the injected launch boundary", async () => {
    for (const channel of ["chromium", "chrome", "msedge"]) {
        const h = hooks();
        h.launch = async (selected) => {
            h.calls.launch++;
            assert.equal(selected, channel);
            return h.browser;
        };
        await R._test.exercise({
            env: env({ R3_BROWSER_CHANNEL: channel }),
            hooks: h,
        });
        assert.equal(h.calls.launch, 1);
        assert.equal(h.calls.browserClose, 1);
    }
});
test("action failure is sanitized; both owners close, no retry/fallback", async () => {
    const h = hooks();
    h.action = async () => {
        h.calls.action++;
        throw new Error(key);
    };
    await assert.rejects(
        R._test.exercise({ env: env(), hooks: h }),
        code("runner-failed"),
    );
    assert.equal(h.calls.launch, 1);
    assert.equal(h.calls.action, 1);
    assert.equal(h.calls.contextClose, 1);
    assert.equal(h.calls.browserClose, 1);
});
test("browser-library TimeoutError is a fixed sanitized timeout code with both owners released", async () => {
    const h = hooks();
    h.action = async () => {
        h.calls.action++;
        const error = new Error(key);
        error.name = "TimeoutError";
        throw error;
    };
    await assert.rejects(
        R._test.exercise({ env: env(), hooks: h }),
        code("timeout"),
    );
    assert.equal(h.calls.launch, 1);
    assert.equal(h.calls.action, 1);
    assert.equal(h.calls.contextClose, 1);
    assert.equal(h.calls.browserClose, 1);
});
test("network and missing-browser failures never retry or launch replacement channel", async () => {
    const h = hooks();
    h.launch = async (channel) => {
        h.calls.launch++;
        assert.equal(channel, "chrome");
        throw new R.Refusal("browser-unavailable");
    };
    await assert.rejects(
        R._test.exercise({ env: env(), hooks: h }),
        code("browser-unavailable"),
    );
    assert.equal(h.calls.launch, 1);
    assert.equal(h.calls.context, 0);
    const n = hooks();
    n.network = async () => {
        n.calls.network++;
        throw new R.Refusal("publication-mismatch");
    };
    await assert.rejects(
        R._test.exercise({ env: env(), hooks: n }),
        code("publication-mismatch"),
    );
    assert.equal(n.calls.network, 1);
    assert.equal(n.calls.launch, 0);
});
test("cancelled held context acquisition closes browser and later context exactly once", async () => {
    const h = hooks(),
        controller = new AbortController();
    let release;
    h.browser.newContext = () => {
        h.calls.context++;
        return new Promise((resolve) => {
            release = resolve;
        });
    };
    const pending = R._test.exercise({
        env: env(),
        hooks: h,
        signal: controller.signal,
    });
    await until(() => release);
    controller.abort();
    await assert.rejects(pending, code("cancelled"));
    assert.equal(h.calls.browserClose, 1);
    assert.equal(h.calls.contextClose, 0);
    release(h.context);
    await until(() => h.calls.contextClose === 1);
    assert.equal(h.calls.contextClose, 1);
    assert.equal(h.calls.launch, 1);
    assert.equal(h.calls.action, 0);
});
test("100s per-wait deadline is finite and action timeout still cleans up without retry", async () => {
    const h = hooks();
    let expire, delay;
    const clock = {
        setTimeout(fn, ms) {
            expire = fn;
            delay = ms;
            return 1;
        },
        clearTimeout() {},
    };
    h.action = () => {
        h.calls.action++;
        return R._test.wait(new Promise(() => {}), null, 100000, clock);
    };
    const pending = R._test.exercise({ env: env(), hooks: h });
    await until(() => expire);
    assert.equal(delay, 100000);
    expire();
    await assert.rejects(pending, code("timeout"));
    assert.equal(h.calls.launch, 1);
    assert.equal(h.calls.action, 1);
    assert.equal(h.calls.contextClose, 1);
    assert.equal(h.calls.browserClose, 1);
});
function request(config, prompt, overrides = {}) {
    const body = {
        model: config.model,
        stream: false,
        options: { num_predict: 4096 },
        messages: [{ role: "user", content: prompt }],
    };
    return {
        url: () => config.endpoint,
        method: () => "POST",
        redirectedFrom: () => null,
        postData: () => JSON.stringify(body),
        allHeaders: async () => ({}),
        ...overrides,
    };
}
test("Ollama observer admits exactly two disclosed requests and records only hashed prompt metadata", async () => {
    const config = R.configuration(env());
    const obs = R._test.observer(config, (error) => {
        throw error;
    });
    const prompt =
        'SYNTHETIC_PAYLOAD {"filename":"main.tex","text":"Україна🙂 е́"}';
    const r = request(config, prompt);
    await assert.rejects(obs.guard(r), code("unexpected-request"));
    obs.arm("chat", prompt);
    await obs.guard(r);
    obs.response({ request: () => r, status: () => 200 });
    await assert.rejects(obs.guard(r), code("unexpected-request"));
    obs.arm("proposal", prompt);
    const p = request(config, prompt);
    await obs.guard(p);
    obs.response({ request: () => p, status: () => 200 });
    assert.throws(() => obs.arm("chat", prompt), code("context-mismatch"));
    const safe = JSON.stringify(obs.records);
    assert.equal(obs.records.length, 2);
    assert.ok(!safe.includes("SYNTHETIC_PAYLOAD"));
    assert.ok(!safe.includes("Україна"));
    assert.equal(obs.records[0].payloadMatchesDisclosure, true);
    assert.equal(obs.records[0].status, 200);
    obs.clear();
});
test("observer refuses extra files, wrong payload/model, redirects, credentials and unapproved destinations", async () => {
    const config = R.configuration(env());
    const prompt = "Synthetic disclosed prompt";
    for (const bad of ["NOT_CONTEXT_SENTINEL", "asset.bin", "приватне/інше.tex"]) {
        const obs = R._test.observer(config, () => {});
        assert.throws(
            () => obs.arm("chat", prompt + bad),
            code("context-mismatch"),
        );
    }
    for (const change of [
        { url: () => "https://other.invalid/" },
        { url: () => config.endpoint + "?key=" + key },
        { url: () => "%%%" },
        { redirectedFrom: () => ({}) },
        { postData: () => '{"other":"' + key + '"}' },
        { allHeaders: async () => ({ authorization: "Bearer " + key }) },
        { allHeaders: async () => ({ cookie: "test=" + key }) },
        { postData: request({ ...config, model: "different" }, prompt).postData },
    ]) {
        const obs = R._test.observer(config, () => {});
        obs.arm("chat", prompt);
        await assert.rejects(obs.guard(request(config, prompt, change)), (error) => {
            assert.ok(error instanceof R.Refusal);
            assert.ok(!String(error).includes(key));
            return true;
        });
        assert.equal(obs.records.length, 0);
    }
});
test("a held header lookup consumes Send before await so a duplicate POST cannot dispatch", async () => {
    const config = R.configuration(env()),
        obs = R._test.observer(config, () => {});
    const prompt = "Synthetic disclosed prompt";
    let release;
    obs.arm("chat", prompt);
    const first = obs.guard(
        request(config, prompt, {
            allHeaders: () =>
                new Promise((resolve) => {
                    release = resolve;
                }),
        }),
    );
    assert.ok(release);
    await assert.rejects(
        obs.guard(request(config, prompt)),
        code("unexpected-request"),
    );
    assert.equal(obs.records.length, 0);
    release({});
    await first;
    assert.equal(obs.records.length, 1);
});
test("nested request extras cannot transmit another file or conversation", async () => {
    const config = R.configuration(env());
    const prompt = "Synthetic disclosed prompt";
    const original = JSON.parse(request(config, prompt).postData());
    original.messages[0].extra = "NOT_CONTEXT_SENTINEL";
    const obs = R._test.observer(config, () => {});
    obs.arm("chat", prompt);
    await assert.rejects(
        obs.guard(request(config, prompt, { postData: () => JSON.stringify(original) })),
        code("wire-mismatch"),
    );
    assert.equal(obs.records.length, 0);
});
test("output symlink cannot redirect a receipt into source", () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "r3-output-symlink-"));
    try {
        fs.symlinkSync(
            path.resolve(__dirname, "../.."),
            path.join(dir, "source"),
            "dir",
        );
        assert.throws(
            () =>
                R.configuration(
                    env({ RESULT_DIR: path.join(dir, "source", "forbidden") }),
                ),
            code("configuration"),
        );
    } finally {
        fs.rmSync(dir, { recursive: true, force: true });
    }
});
test("publication drift refuses and revision comparisons preserve unrelated file bytes", () => {
    const a = { indexSha256: "a", assetsSha256: "b" };
    R._test.samePublication(a, { ...a });
    assert.throws(
        () => R._test.samePublication(a, { ...a, assetsSha256: "c" }),
        code("publication-mismatch"),
    );
    const original = {
        files: [
            { id: 1, path: "main.tex", bytes: [65] },
            { id: 2, path: "asset.bin", bytes: [0, 255] },
        ],
    };
    R._test.headMatches(
        {
            files: [
                { id: 1, path: "main.tex", bytes: [66] },
                original.files[1],
            ],
        },
        original,
        Buffer.from([66]),
    );
    assert.throws(
        () =>
            R._test.headMatches(
                {
                    files: [
                        { id: 1, path: "main.tex", bytes: [66] },
                        { ...original.files[1], bytes: [0] },
                    ],
                },
                original,
                Buffer.from([66]),
            ),
        code("source-mismatch"),
    );
});
test("publication preflight pins real bytes without evaluating manifest JS; redirects/mismatch stop before launch", async () => {
    const settings = env(),
        config = R.configuration(settings),
        signal = new AbortController().signal;
    const index = Buffer.from("Synthetic published index");
    const manifest = Buffer.from(
        "self.assetsManifest = " +
            JSON.stringify({
                version: "synthetic_v1",
                assets: [
                    {
                        url: "index.html",
                        hash:
                            "sha256-" +
                            createHash("sha256").update(index).digest("base64"),
                    },
                ],
            }) +
            ";",
    );
    for (const fault of [
        null,
        "redirect",
        "wrong-origin",
        "wrong-index-hash",
        "executable-manifest",
        "oversize-header",
    ]) {
        const h = hooks();
        let fetched = 0;
        h.network = async () => {
            h.calls.network++;
            return R._test.publication({
                config,
                signal,
                fetcher: async (url, options) => {
                    fetched++;
                    assert.equal(options.redirect, "error");
                    assert.equal(options.credentials, "omit");
                    assert.equal(options.cache, "no-store");
                    assert.ok(!JSON.stringify(options).includes(key));
                    const data = url.endsWith(".js")
                        ? fault === "executable-manifest"
                            ? Buffer.from(
                                  'self.assetsManifest = (() => { throw Error("never execute"); })();',
                              )
                            : manifest
                        : fault === "wrong-index-hash"
                          ? Buffer.from("changed")
                          : index;
                    const response = new Response(data, {
                        status: fault === "redirect" ? 302 : 200,
                        headers:
                            fault === "oversize-header"
                                ? { "content-length": "999999999" }
                                : {},
                    });
                    Object.defineProperty(response, "url", {
                        value:
                            fault === "wrong-origin"
                                ? "https://other.invalid/"
                                : url,
                    });
                    return response;
                },
            });
        };
        if (fault) {
            await assert.rejects(
                R._test.exercise({ env: settings, hooks: h }),
                code("publication-mismatch"),
            );
            assert.equal(h.calls.launch, 0);
            assert.ok(fetched <= 2);
        } else {
            await R._test.exercise({ env: settings, hooks: h });
            assert.equal(h.calls.launch, 1);
            assert.equal(fetched, 2);
        }
    }
    assert.throws(
        () =>
            R._test.publication({
                argv: ["--allow-live"],
                config,
                signal,
                fetcher: () => assert.fail("not reached"),
            }),
        code("test-hook-forbidden"),
    );
});
test("real CLI default/help and invalid live configuration cannot print secrets or create a result artifact", () => {
    const output = fs.mkdtempSync(path.join(os.tmpdir(), "r3-guard-"));
    try {
        for (const [args, stdin] of [
            [[], ""],
            [["--help"], key],
            [["--allow-live"], '{"apiKey":"' + key + '","extra":1}\n'],
        ]) {
            const child = spawnSync(process.execPath, [script, ...args], {
                env: env({ APP_URL: "https://app.example.invalid/path", RESULT_DIR: output }),
                input: stdin,
                encoding: "utf8",
                timeout: 10000,
            });
            assert.equal(child.error, undefined);
            assert.equal(child.signal, null);
            assert.ok(!child.stdout.includes(key));
            assert.ok(!child.stderr.includes(key));
            assert.equal(child.status, args[0] === "--help" ? 0 : 1);
            assert.deepEqual(fs.readdirSync(output), []);
        }
    } finally {
        fs.rmSync(output, { recursive: true, force: true });
    }
});
