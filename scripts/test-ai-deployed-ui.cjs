// Opt-in acceptance tooling, NOT product/provider logic. Real UI owns all edits.
// No top-level Playwright import, network, stdin read or filesystem writes.
const fs = require("node:fs");
const path = require("node:path");
const { createHash, randomUUID } = require("node:crypto");
const { isDeepStrictEqual } = require("node:util");
const WAIT_MS = 100000;
const SOURCE_ROOT = path.resolve(__dirname, "..");
const FIXTURE_HASH =
    "719163f50c023f5aeccc60d68ed8f29b670960d2a61b07bc91c8df0ee6ed414f";
const sha = (bytes) => createHash("sha256").update(bytes).digest("hex");
class Refusal extends Error {
    constructor(code) {
        super(code);
        this.code = code;
    }
}
const requireThat = (ok, code) => {
    if (!ok) throw new Refusal(code);
};
function safeCode(error) {
    if (error instanceof Refusal) return error.code;
    if (error instanceof Error && error.name === "TimeoutError")
        return "timeout";
    return "runner-failed";
}
const HELP = `Opt-in deployed Ollama acceptance (two real requests).
Usage: node scripts/test-ai-deployed-ui.cjs --allow-live
Required env: APP_URL (canonical HTTPS origin, no trailing slash), R3_MODEL,
R3_BROWSER_CHANNEL (chromium|chrome|msedge),
RESULT_DIR (private absolute evidence directory outside source).
Optional: R3_OLLAMA_URL (http://localhost:11434 or http://127.0.0.1:11434).
The runner uses a synthetic local project and takes no input on stdin.
--help has no side effects. Default invocation refuses before network or launch.
`;
function httpsOrigin(value) {
    requireThat(
        typeof value === "string" && value.length <= 256,
        "configuration",
    );
    let u;
    try {
        u = new URL(value);
    } catch {
        throw new Refusal("configuration");
    }
    requireThat(
        u.protocol === "https:" &&
            value === u.origin &&
            !u.username &&
            !u.password &&
            !u.search &&
            !u.hash,
        "configuration",
    );
    return u;
}
function configuration(env) {
    // Playwright/Node debug logging can expose browser protocol data.
    // Refuse inherited instrumentation rather than silently changing host policy.
    requireThat(
        [
            "DEBUG",
            "PWDEBUG",
            "NODE_DEBUG",
            "NODE_DEBUG_NATIVE",
            "NODE_OPTIONS",
        ].every((name) => !env[name]),
        "unsafe-instrumentation",
    );
    const allowed = new Set([
        "R3_MODEL",
        "R3_OLLAMA_URL",
        "R3_BROWSER_CHANNEL",
    ]);
    for (const name of Object.keys(env)) {
        requireThat(
            !name.startsWith("R3_") || allowed.has(name),
            "configuration",
        );
        requireThat(
            !/(?:^|_)(?:API_?KEY|ACCESS_TOKEN|AUTH_TOKEN|CLIENT_SECRET|PASSWORD|CREDENTIALS?)$/i.test(
                name,
            ),
            "credential-source",
        );
    }
    const app = httpsOrigin(env.APP_URL).origin;
    const model = env.R3_MODEL;
    const channel = env.R3_BROWSER_CHANNEL;
    requireThat(
        typeof model === "string" &&
            /^[A-Za-z0-9_.:/-]{1,128}$/.test(model) &&
            !model.includes("://") &&
            !model.startsWith("/"),
        "configuration",
    );
    requireThat(
        ["chromium", "chrome", "msedge"].includes(channel),
        "configuration",
    );
    const local = env.R3_OLLAMA_URL ?? "http://localhost:11434";
    requireThat(
        ["http://localhost:11434", "http://127.0.0.1:11434"].includes(local),
        "configuration",
    );
    const dir = env.RESULT_DIR;
    requireThat(
        typeof dir === "string" &&
            dir.length <= 4096 &&
            !/[\x00-\x1f\x7f]/.test(dir) &&
            path.isAbsolute(dir),
        "configuration",
    );
    const output = path.resolve(dir);
    let ancestor = output;
    while (!fs.existsSync(ancestor) && ancestor !== path.dirname(ancestor))
        ancestor = path.dirname(ancestor);
    let canonical;
    try {
        canonical = path.join(
            fs.realpathSync(ancestor),
            path.relative(ancestor, output),
        );
    } catch {
        throw new Refusal("configuration");
    }
    requireThat(
        canonical === output &&
            output !== SOURCE_ROOT &&
            !output.startsWith(SOURCE_ROOT + path.sep),
        "configuration",
    );
    return Object.freeze({
        app,
        model,
        channel,
        output,
        local,
        endpoint: local + "/api/chat",
    });
}
function wait(promise, signal, milliseconds = WAIT_MS, timers = globalThis) {
    return new Promise((resolve, reject) => {
        let timer;
        const finish = (error, value) => {
            timers.clearTimeout(timer);
            signal?.removeEventListener("abort", abort);
            if (error) reject(error);
            else resolve(value);
        };
        const abort = () => finish(new Refusal("cancelled"));
        if (signal?.aborted) {
            abort();
            return;
        }
        signal?.addEventListener("abort", abort, { once: true });
        if (milliseconds !== null)
            timer = timers.setTimeout(
                () => finish(new Refusal("timeout")),
                milliseconds,
            );
        Promise.resolve(promise).then(
            (value) => finish(null, value),
            (error) => finish(error),
        );
    });
}
async function close(resource) {
    if (!resource) return true;
    try {
        await wait(resource.close(), null, 5000);
        return true;
    } catch {
        return false;
    }
}
async function acquire(factory, signal) {
    requireThat(!signal?.aborted, "cancelled");
    let abandoned = false,
        delivered = null;
    const pending = Promise.resolve()
        .then(factory)
        .then((value) => {
            delivered = value;
            if (abandoned) {
                delivered = null;
                void close(value);
            }
            return value;
        });
    try {
        const value = await wait(pending, signal);
        delivered = null;
        return value;
    } catch (error) {
        abandoned = true;
        if (delivered) {
            void close(delivered);
            delivered = null;
        }
        throw error;
    }
}
async function browserSession(config, signal, boundary, action) {
    let browser, context;
    let cleanup = true;
    try {
        browser = await acquire(() => boundary.launch(config.channel), signal);
        context = await acquire(
            () => browser.newContext({ serviceWorkers: "allow" }),
            signal,
        );
        const page = await acquire(() => context.newPage(), signal);
        page.setDefaultTimeout(WAIT_MS);
        page.setDefaultNavigationTimeout(WAIT_MS);
        // UI/API waits below each have a 100s cap; two separately authorized
        // 90s provider calls must not share an accidental 100s whole-run budget.
        return await wait(action({ browser, context, page }), signal, null);
    } finally {
        cleanup = await close(context);
        cleanup = (await close(browser)) && cleanup;
        requireThat(cleanup, "cleanup-failed");
    }
}
async function publication(config, signal, fetcher = fetch) {
    const read = async (relative, budget) => {
        let response;
        try {
            response = await fetcher(config.app + relative, {
                redirect: "error",
                cache: "no-store",
                credentials: "omit",
                signal: AbortSignal.any([signal, AbortSignal.timeout(WAIT_MS)]),
            });
            requireThat(
                response.status === 200 &&
                    response.url === config.app + relative &&
                    response.body,
                "publication-mismatch",
            );
            requireThat(
                Number(response.headers.get("content-length") || 0) <= budget,
                "publication-mismatch",
            );
            const parts = [];
            let size = 0;
            for await (const chunk of response.body) {
                size += chunk.length;
                requireThat(size <= budget, "publication-mismatch");
                parts.push(chunk);
            }
            return Buffer.concat(parts);
        } catch {
            throw new Refusal(
                signal.aborted ? "cancelled" : "publication-mismatch",
            );
        } finally {
            await response?.body?.cancel().catch(() => {});
        }
    };
    const index = await read("/", 1024 * 1024);
    const assets = await read("/service-worker-assets.js", 4 * 1024 * 1024);
    const match = /^\s*self\.assetsManifest\s*=\s*(\{[\s\S]*\})\s*;?\s*$/.exec(
        assets.toString("utf8"),
    );
    requireThat(match, "publication-mismatch");
    let manifest;
    try {
        manifest = JSON.parse(match[1]);
    } catch {
        throw new Refusal("publication-mismatch");
    }
    requireThat(
        manifest &&
            typeof manifest.version === "string" &&
            /^[A-Za-z0-9+/_-]{1,128}={0,2}$/.test(manifest.version) &&
            Array.isArray(manifest.assets) &&
            manifest.assets.length > 0 &&
            manifest.assets.length <= 10000 &&
            manifest.assets.every(
                (a) =>
                    a &&
                    typeof a.url === "string" &&
                    typeof a.hash === "string",
            ),
        "publication-mismatch",
    );
    const entry = manifest.assets.filter((a) => a.url === "index.html");
    requireThat(
        entry.length === 1 &&
            entry[0].hash ===
                "sha256-" + createHash("sha256").update(index).digest("base64"),
        "publication-mismatch",
    );
    return { indexSha256: sha(index), assetsSha256: sha(assets) };
}
function samePublication(a, b) {
    requireThat(
        a.indexSha256 === b.indexSha256 && a.assetsSha256 === b.assetsSha256,
        "publication-mismatch",
    );
}
function requestPrompt(data, model) {
    const prompt = data?.messages?.[0]?.content;
    requireThat(typeof prompt === "string", "wire-mismatch");
    const expected = {
        model,
        stream: false,
        options: { num_predict: 4096 },
        messages: [{ role: "user", content: prompt }],
    };
    requireThat(isDeepStrictEqual(data, expected), "wire-mismatch");
    return prompt;
}
function observer(config, fail) {
    let ticket = null;
    const requests = new WeakMap();
    const records = [];
    return {
        records,
        arm(operation, prompt) {
            requireThat(
                ["chat", "proposal"].includes(operation) &&
                    !ticket &&
                    records.length === (operation === "chat" ? 0 : 1) &&
                    typeof prompt === "string" &&
                    Buffer.byteLength(prompt) <= 77824 &&
                    !prompt.includes("NOT_CONTEXT_SENTINEL") &&
                    !prompt.includes("asset.bin") &&
                    !prompt.includes("приватне/інше.tex"),
                "context-mismatch",
            );
            ticket = { operation, prompt };
        },
        async guard(request) {
            const url = request.url();
            let parsed;
            try {
                parsed = new URL(url);
            } catch {
                throw new Refusal("unexpected-network");
            }
            if (["blob:", "data:"].includes(parsed.protocol)) return;
            if (parsed.origin === config.app && url !== config.endpoint) {
                requireThat(
                    ["GET", "HEAD"].includes(request.method()) &&
                        !request.redirectedFrom(),
                    "unexpected-network",
                );
                return;
            }
            requireThat(
                url === config.endpoint &&
                    ["POST", "OPTIONS"].includes(request.method()),
                "unexpected-network",
            );
            if (request.method() === "OPTIONS") return;
            requireThat(
                ticket !== null && !request.redirectedFrom(),
                "unexpected-request",
            );
            const ownedTicket = ticket;
            ticket = null; // consume BEFORE asynchronous header lookup; no parallel duplicate POST
            const body = request.postData();
            requireThat(
                typeof body === "string" &&
                    Buffer.byteLength(body) <= 524288,
                "wire-mismatch",
            );
            let data;
            try {
                data = JSON.parse(body);
            } catch {
                throw new Refusal("wire-mismatch");
            }
            const prompt = requestPrompt(data, config.model);
            requireThat(prompt === ownedTicket.prompt, "context-mismatch");
            const headers = await request.allHeaders();
            requireThat(!headers.cookie && !headers.authorization, "wire-mismatch");
            const record = {
                endpointClass: "local-chat",
                provider: "Ollama",
                model: config.model,
                operation: ownedTicket.operation,
                promptSha256: sha(prompt),
                payloadMatchesDisclosure: true,
                status: null,
                elapsedMs: null,
            };
            records.push(record);
            requests.set(request, { record, start: performance.now() });
        },
        response(response) {
            const item = requests.get(response.request());
            if (!item) return;
            item.record.status = response.status();
            item.record.elapsedMs = Math.round(performance.now() - item.start);
            if (response.status() < 200 || response.status() >= 300)
                fail(new Refusal("provider-http-failure"));
        },
        clear() {
            ticket = null;
        },
    };
}
async function fixtureProject(page, H, source) {
    const name = "R3 synthetic AI " + randomUUID();
    await H.reveal(page, page.locator("#new-project"));
    await page.locator("#new-project").fill(name);
    await page
        .getByRole("button", { name: "Create project", exact: true })
        .click();
    const put = async (name, buffer) => {
        await H.$(page, "import-input").setInputFiles({
            name,
            mimeType: "application/octet-stream",
            buffer,
        });
        const selected = H.$(page, "select-file").filter({ hasText: name });
        await selected.waitFor();
        await selected.click();
    };
    await put("asset.bin", Buffer.from([0, 255, 16, 127]));
    await put("інше.tex", Buffer.from("NOT_CONTEXT_SENTINEL\n"));
    await H.$(page, "rename-input").fill("приватне/інше.tex");
    await H.$(page, "rename").click();
    await H.$(page, "select-file")
        .filter({ hasText: "приватне/інше.tex" })
        .waitFor();
    await put("main.tex", source);
    await H.save(page);
    return name;
}
function headMatches(head, original, mainBytes) {
    requireThat(head.files.length === original.files.length, "source-mismatch");
    for (const file of original.files) {
        const current = head.files.find(
            (f) => f.id === file.id && f.path === file.path,
        );
        requireThat(
            current &&
                Buffer.from(current.bytes).equals(
                    file.path === "main.tex"
                        ? mainBytes
                        : Buffer.from(file.bytes),
                ),
            "source-mismatch",
        );
    }
}
async function live(config, signal, result, controller) {
    // Root binds this first same-origin snapshot to its approved release receipt.
    // The runner verifies served navigation and refuses any subsequent drift.
    let engine;
    try {
        engine = require("playwright").chromium;
    } catch {
        throw new Refusal("browser-unavailable");
    }
    const pin = await wait(publication(config, signal), signal);
    result.publication = pin;
    const boundary = {
        launch: async (channel) => {
            try {
                return await engine.launch({
                    headless: true,
                    timeout: WAIT_MS,
                    ...(channel === "chromium" ? {} : { channel }),
                });
            } catch {
                throw new Refusal("browser-unavailable");
            }
        },
    };
    const H = require("./review-ui-helpers.cjs");
    const source = fs.readFileSync(
        path.join(SOURCE_ROOT, "tests/fixtures/r3-live/main.tex"),
    );
    requireThat(sha(source) === FIXTURE_HASH, "fixture-mismatch");
    const expected = Buffer.from(
        source.toString("utf8").replace("ALPHA", "BETA"),
    );
    let observe;
    try {
        await browserSession(
            config,
            signal,
            boundary,
            async ({ browser, context, page }) => {
                const version = browser.version();
                requireThat(
                    /^\d+\.\d+\.\d+\.\d+$/.test(version),
                    "browser-unavailable",
                );
                result.browserVersion = version;
                const fatal = (error) => {
                    if (!controller.signal.aborted) controller.abort(error);
                };
                observe = observer(config, fatal);
                result.requests = observe.records; // retain safe dispatched counts even on failure
                // Abort-only policy guard: no response fulfillment, injection or mocks.
                await context.route("**/*", async (route) => {
                    try {
                        requireThat(!signal.aborted, "cancelled");
                        await observe.guard(route.request());
                        requireThat(!signal.aborted, "cancelled");
                        await route.continue();
                    } catch (error) {
                        fatal(
                            error instanceof Refusal
                                ? error
                                : new Refusal("unexpected-network"),
                        );
                        await route.abort().catch(() => {});
                    }
                });
                const response = (r) => observe.response(r);
                const pageError = () => fatal(new Refusal("browser-error"));
                const navigation = (frame) => {
                    if (
                        frame === page.mainFrame() &&
                        frame.url() !== config.app + "/"
                    )
                        fatal(new Refusal("origin-mismatch"));
                };
                context.on("response", response);
                page.on("pageerror", pageError);
                page.on("framenavigated", navigation);
                try {
                    const initial = await page.goto(config.app + "/", {
                        waitUntil: "domcontentloaded",
                    });
                    requireThat(
                        initial &&
                            initial.status() === 200 &&
                            initial.url() === config.app + "/" &&
                            !initial.request().redirectedFrom() &&
                            sha(await wait(initial.body(), signal)) ===
                                pin.indexSha256,
                        "publication-mismatch",
                    );
                    await H.ready(page);
                    const name = await wait(
                        fixtureProject(page, H, source),
                        signal,
                    );
                    const readHead = () => wait(H.head(page, name), signal);
                    const before = await readHead();
                    requireThat(
                        before.files.length === 3 &&
                            Buffer.from(
                                before.files.find((f) => f.path === "main.tex")
                                    ?.bytes || [],
                            ).equals(source),
                        "fixture-mismatch",
                    );
                    const ai = H.$(page, "ai-panel");
                    await H.reveal(page, H.$(page, "ai-model"));
                    await H.$(page, "ai-model").fill(config.model);
                    await H.$(page, "ai-local-url").fill(config.local);
                    await H.$(page, "ai-local-url").blur();
                    await H.$(page, "ai-connect").click();
                    await H.$(page, "ai-destination").waitFor();
                    const destination = await H.$(
                        page,
                        "ai-destination",
                    ).textContent();
                    requireThat(
                        destination.includes("Ollama") &&
                            destination.includes(config.model) &&
                            destination.includes(config.endpoint),
                        "destination-mismatch",
                    );
                    await ai
                        .getByRole("button", {
                            name: "Prepare active-file context",
                            exact: true,
                        })
                        .click();
                    await H.$(page, "ai-context").waitFor();
                    requireThat(
                        (await H.$(page, "ai-context").textContent()) ===
                            source.toString("utf8"),
                        "context-mismatch",
                    );
                    const send = async (operation, instruction) => {
                        await ai
                            .getByRole("combobox", { name: "Request type" })
                            .selectOption(operation === "proposal" ? "Proposal" : "Question");
                        await ai
                            .getByRole("textbox", {
                                name: "Your request",
                                exact: false,
                            })
                            .fill(instruction);
                        await H.reveal(page, H.$(page, "ai-payload"));
                        const prompt = await H.$(
                            page,
                            "ai-payload",
                        ).textContent();
                        requireThat(
                            typeof prompt === "string" &&
                                prompt.includes('"filename":"main.tex"'),
                            "context-mismatch",
                        );
                        samePublication(
                            pin,
                            await wait(publication(config, signal), signal),
                        );
                        requireThat(
                            page.url() === config.app + "/",
                            "origin-mismatch",
                        );
                        observe.arm(operation, prompt);
                        await ai
                            .getByRole("button", {
                                name: "Send disclosed request",
                                exact: true,
                            })
                            .click();
                        const success =
                            operation === "chat"
                                ? "Answer received. Nothing has been edited."
                                : "Proposal received, not applied. Preview it explicitly in the existing review workflow.";
                        await page.waitForFunction(
                            (success) => {
                                const status = document.querySelector(
                                    '[data-testid="ai-status"]',
                                )?.textContent;
                                return (
                                    status === success ||
                                    status?.startsWith(
                                        "Response is display-only:",
                                    ) ||
                                    /^(Cannot reach|Ollama |The request timed|The context,|Context changed|The requested model)/.test(
                                        status || "",
                                    )
                                );
                            },
                            success,
                            { timeout: WAIT_MS },
                        );
                        requireThat(
                            (await H.$(page, "ai-status").textContent()) ===
                                success,
                            operation === "proposal"
                                ? "invalid-proposal"
                                : "provider-failure",
                        );
                        requireThat(
                            (await H.$(page, "ai-answer").textContent())?.trim()
                                .length > 0,
                            "provider-failure",
                        );
                        const after = await readHead();
                        requireThat(after.id === before.id, "source-mismatch");
                        headMatches(after, before, source);
                        requireThat(
                            (await H.$(page, "review-preview").count()) === 0,
                            "unexpected-review",
                        );
                    };
                    await send(
                        "chat",
                        "Acknowledge this synthetic file briefly as plain text. Do not propose edits.",
                    );
                    result.chatUnchanged = true;
                    await send(
                        "proposal",
                        "Replace only ASCII ALPHA with BETA. Preserve every other character and line ending of the supplied file. Return the prescribed JSON object only.",
                    );
                    const preview = ai.getByRole("button", {
                        name: "Preview proposal in Review (not acceptance)",
                        exact: true,
                    });
                    requireThat(await preview.isEnabled(), "invalid-proposal");
                    await preview.click();
                    await H.$(page, "review-preview").waitFor();
                    await H.idle(page);
                    requireThat(
                        (await H.$(page, "review-after").textContent()) ===
                            expected.toString("utf8"),
                        "invalid-proposal",
                    );
                    const unaccepted = await readHead();
                    requireThat(unaccepted.id === before.id, "source-mismatch");
                    headMatches(unaccepted, before, source);
                    await H.reveal(page, H.$(page, "review-accept"));
                    await H.accept(page);
                    const applied = await readHead();
                    headMatches(applied, before, expected);
                    requireThat(
                        applied.id !== before.id &&
                            applied.parent === before.id,
                        "revision-mismatch",
                    );
                    await H.undo(page);
                    const restored = await readHead();
                    headMatches(restored, before, source);
                    requireThat(
                        restored.id !== applied.id &&
                            restored.parent === applied.id,
                        "revision-mismatch",
                    );
                    result.previewUnchanged = true;
                    result.acceptedBytesMatch = true;
                    result.undoBytesMatch = true;
                    result.acceptParentMatches = true;
                    result.undoParentMatches = true;
                    result.originalSha256 = sha(source);
                    result.acceptedSha256 = sha(expected);
                    await ai
                        .getByRole("button", {
                            name: "Disconnect and clear memory",
                            exact: true,
                        })
                        .click();
                    requireThat(
                        (await H.$(page, "ai-context").count()) === 0 &&
                            (await H.$(page, "ai-answer").count()) === 0 &&
                            (await H.$(page, "ai-destination").count()) === 0,
                        "disconnect-failed",
                    );
                    result.disconnected = true;
                    requireThat(
                        observe.records.length === 2 &&
                            observe.records.every(
                                (r) => r.status >= 200 && r.status < 300,
                            ),
                        "request-count",
                    );
                    result.requests = observe.records;
                } finally {
                    if (page.url() === config.app + "/") {
                        try {
                            await aiDisconnect(page, H);
                        } catch {
                            /* context/browser are always closed below */
                        }
                    }
                    context.off("response", response);
                    page.off("pageerror", pageError);
                    page.off("framenavigated", navigation);
                    observe.clear();
                }
            },
        );
    } finally {
        observe?.clear();
    }
}
async function aiDisconnect(page, H) {
    await H.$(page, "ai-panel")
        .getByRole("button", {
            name: "Disconnect and clear memory",
            exact: true,
        })
        .click({ timeout: 5000 });
}
async function main(options = {}) {
    const argv = options.argv ?? process.argv.slice(2);
    const out = options.stdout ?? process.stdout;
    if (argv.length === 1 && argv[0] === "--help") {
        out.write(HELP);
        return 0;
    }
    const controller = new AbortController();
    const cancel = () => controller.abort(new Refusal("cancelled"));
    try {
        requireThat(
            argv.length === 1 && argv[0] === "--allow-live",
            "live-opt-in-required",
        );
        // Dependency injection is test-only and is categorically forbidden for
        // the live entrypoint: a hook can never replace a real provider/browser.
        requireThat(!options.testHooks, "test-hook-forbidden");
        options.signal?.addEventListener("abort", cancel, { once: true });
        if (options.signal?.aborted) cancel();
        process.once("SIGINT", cancel);
        process.once("SIGTERM", cancel);
        requireThat(!controller.signal.aborted, "cancelled");
        const config = configuration(options.env ?? process.env);
        const result = {
            schemaVersion: 1,
            milestone: "R3",
            evidenceClass: "live-browser",
            passed: false,
            provider: "Ollama",
            model: config.model,
            channel: config.channel,
            supplementalChromium: config.channel === "chromium",
            code: "running",
            requests: [],
        };
        const started = performance.now();
        try {
            await live(config, controller.signal, result, controller);
            result.passed = true;
            result.code = "passed";
        } catch (error) {
            result.code = safeCode(
                controller.signal.reason instanceof Refusal
                    ? controller.signal.reason
                    : error,
            );
            controller.abort(new Refusal(result.code));
        }
        result.elapsedMs = Math.round(performance.now() - started);
        // Only allowlisted fields above are serialized. No raw exception,
        // origin/path, headers, request/response bodies or browser storage state.
        const serialized = JSON.stringify(result, null, 2) + "\n";
        try {
            fs.mkdirSync(config.output, { recursive: true, mode: 0o700 });
            fs.writeFileSync(
                path.join(config.output, "receipt.json"),
                serialized,
                { flag: "wx", mode: 0o600 },
            );
        } catch {
            throw new Refusal("output-failed");
        }
        out.write(
            JSON.stringify({ passed: result.passed, code: result.code }) + "\n",
        );
        return result.passed ? 0 : 1;
    } catch (error) {
        out.write(
            JSON.stringify({ passed: false, code: safeCode(error) }) + "\n",
        );
        return 1;
    } finally {
        process.off("SIGINT", cancel);
        process.off("SIGTERM", cancel);
        options.signal?.removeEventListener("abort", cancel);
    }
}
// Builtin-only contract tests can exercise resource ownership and injected
// launch/network boundaries, but this path accepts NO live flag and emits no
// live receipt/files. The production entrypoint never invokes these hooks.
async function testExercise({ argv = [], env, signal, hooks }) {
    requireThat(
        argv.length === 0 &&
            hooks &&
            ["network", "launch", "action"].every(
                (name) => typeof hooks[name] === "function",
            ),
        "test-hook-forbidden",
    );
    requireThat(!signal?.aborted, "cancelled");
    const config = configuration(env);
    try {
        await wait(hooks.network(), signal);
        return await browserSession(
            config,
            signal,
            hooks,
            hooks.action,
        );
    } catch (error) {
        throw new Refusal(safeCode(error));
    }
}
module.exports = {
    main,
    configuration,
    Refusal,
    _test: {
        exercise: testExercise,
        wait,
        acquire,
        observer,
        requestPrompt,
        samePublication,
        headMatches,
        publication: ({ argv = [], config, signal, fetcher }) => {
            requireThat(
                argv.length === 0 && typeof fetcher === "function",
                "test-hook-forbidden",
            );
            return publication(config, signal, fetcher);
        },
    },
};
if (require.main === module) {
    main().then(
        (code) => {
            process.exitCode = code;
        },
        () => {
            process.stdout.write('{"passed":false,"code":"runner-failed"}\n');
            process.exitCode = 1;
        },
    );
}
