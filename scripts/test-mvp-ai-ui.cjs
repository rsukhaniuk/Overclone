// Published app UI with synthetic Ollama responses. Run with external networking disabled.
const assert = require("node:assert/strict");
const { chromium } = require("playwright");
const H = require("./review-ui-helpers.cjs");
const base = process.env.APP_URL || "http://127.0.0.1:8080";
const origin = new URL(base).origin;
const model = "ui-test-local";
const endpoint = "http://localhost:11434/api/chat";
const tags = "http://localhost:11434/api/tags";

function mockResponse(request, text) {
    const body = request.postDataJSON(), headers = request.headers();
    assert.equal(body.model, model);
    assert.equal(body.stream, false);
    assert.equal(body.messages.length, 1);
    assert.equal(body.messages[0].role, "user");
    assert.equal(headers.authorization, undefined);
    assert.equal(headers.cookie, undefined);
    return { prompt: body.messages[0].content, response: { done: true, message: { content: text } } };
}

(async () => {
    const browser = await chromium.launch({ headless: true });
    const context = await browser.newContext({ serviceWorkers: "block" });
    const page = await context.newPage();
    page.setDefaultTimeout(15000);
    const errors = [], external = [], requests = [];
    let ollamaUnavailable = false, catalogCalls = 0;
    page.on("pageerror", e => { if (errors.length < 32) errors.push(e.message); });
    page.on("request", r => {
        if (/^https?:/.test(r.url()) && new URL(r.url()).origin !== origin)
            external.push(r.url());
    });
    const original = "\\documentclass{article}\n\\begin{document}\nУкраїна🙂\n\\end{document}\n";
    const replacement = original + "% Reviewed AI suggestion🙂\n";
    const chat = "Synthetic acknowledgement.";
    const proposal = JSON.stringify({ replacementText: replacement, explanation: "Synthetic review proposal" });
    await context.route(/^https?:/, route => new URL(route.request().url()).origin === origin ? route.continue() : route.abort());
    await page.route(tags, async route => {
        const request = route.request();
        assert.equal(request.method(), "GET");
        assert.equal(request.postData(), null);
        assert.equal(request.headers().cookie, undefined);
        assert.equal(request.headers().authorization, undefined);
        if (++catalogCalls === 1) { await route.abort(); return; }
        await route.fulfill({ headers: { "access-control-allow-origin": origin }, json: {
            models: [{ name: model }, { model: "alternate:7b" }, { name: "remote:cloud" }],
        } });
    });
    await page.route(endpoint, async route => {
        const request = route.request();
        const headers = { "access-control-allow-origin": origin, "access-control-allow-headers": "content-type", "access-control-allow-methods": "POST,OPTIONS", "cache-control": "no-store" };
        if (request.method() === "OPTIONS") { await route.fulfill({ status: 204, headers }); return; }
        assert.equal(request.method(), "POST");
        const reply = mockResponse(request, requests.length === 0 ? chat : proposal);
        requests.push(reply.prompt);
        if (ollamaUnavailable) { await route.abort(); return; }
        await route.fulfill({ headers, contentType: "application/json", body: JSON.stringify(reply.response) });
    });
    try {
        await page.goto(base, { waitUntil: "domcontentloaded" });
        await H.ready(page);
        assert.deepEqual(external, [], "startup must not contact optional services");
        const name = "AI UI regression";
        await H.reveal(page, page.locator("#new-project"));
        await page.locator("#new-project").fill(name);
        await page.getByRole("button", { name: "Create project", exact: true }).click();
        await H.$(page, "import-input").setInputFiles({ name: "main.tex", mimeType: "text/plain", buffer: Buffer.from(original) });
        await page.locator(".editor-toolbar label", { hasText: "main.tex" }).waitFor();
        if (await H.$(page, "save").isEnabled()) await H.save(page);
        const ai = H.$(page, "ai-panel");
        await H.reveal(page, ai);
        assert.ok(await ai.isVisible());
        assert.equal(await H.$(page, "editor").isVisible(), false);
        assert.equal(await H.$(page, "ai-provider").count(), 0);
        assert.equal(await H.$(page, "ai-key").count(), 0);
        assert.deepEqual(external, [], "opening AI does not discover models or send prompts");
        await H.$(page, "ai-discover-models").click();
        await H.$(page, "ai-discovery-status").filter({ hasText: "Could not list models" }).waitFor();
        await H.reveal(page, H.$(page, "ai-model"));
        await H.$(page, "ai-model").fill(model);
        assert.ok(await H.$(page, "ai-connect").isEnabled(), "manual entry works after discovery fails");
        await H.$(page, "ai-model").fill("");
        await H.$(page, "ai-discover-models").click();
        const installed = H.$(page, "ai-installed-models");
        await installed.waitFor();
        assert.deepEqual(await installed.locator("option").allTextContents(), ["Choose a model", "alternate:7b", model]);
        await installed.selectOption(model);
        await H.$(page, "ai-connect").click();
        await H.$(page, "ai-destination").waitFor();
        assert.match(await H.$(page, "ai-destination").textContent(), /Ollama/);
        assert.equal(requests.length, 0);
        await ai.getByRole("button", { name: "Prepare active-file context", exact: true }).click();
        await H.$(page, "ai-context").waitFor();
        assert.equal(await H.$(page, "ai-context").textContent(), original);
        assert.match(await H.$(page, "ai-destination").textContent(), /ui-test-local.*localhost:11434/s);
        assert.deepEqual(external, [tags, tags], "explicit discovery makes the only requests before Send");
        const before = await H.head(page, name);
        const instruction = ai.getByRole("textbox", { name: "Your request", exact: false });
        const send = ai.getByRole("button", { name: "Send disclosed request", exact: true });
        await instruction.fill("Acknowledge the synthetic file");
        await H.$(page, "open-editor").click();
        assert.ok(await H.$(page, "editor").isVisible());
        assert.equal(await ai.isVisible(), false);
        await H.reveal(page, ai);
        assert.equal(await instruction.inputValue(), "Acknowledge the synthetic file");
        const chatPrompt = await H.$(page, "ai-payload").textContent();
        await send.click();
        await H.$(page, "ai-answer").filter({ hasText: chat }).waitFor();
        assert.deepEqual(requests, [chatPrompt]);
        await H.$(page, "open-editor").click();
        await H.reveal(page, ai);
        assert.equal(await instruction.inputValue(), "Acknowledge the synthetic file");
        assert.equal(await H.$(page, "ai-answer").textContent(), chat);
        assert.deepEqual(await H.head(page, name), before);
        assert.equal(await H.$(page, "review-preview").count(), 0);
        await instruction.fill("Propose a small correction");
        await ai.getByRole("checkbox", { name: "Request an edit proposal", exact: false }).check();
        const editPrompt = await H.$(page, "ai-payload").textContent();
        await send.click();
        await H.$(page, "ai-answer").filter({ hasText: "Synthetic review proposal" }).waitFor();
        assert.deepEqual(requests, [chatPrompt, editPrompt]);
        assert.equal(await H.$(page, "editor").inputValue(), original);
        assert.equal(await H.$(page, "review-preview").count(), 0, "reply alone cannot create a review or apply edits");
        await ai.getByRole("button", { name: "Review proposed edits", exact: true }).click();
        await H.$(page, "review-preview").waitFor();
        await H.idle(page);
        assert.equal(await H.$(page, "editor").inputValue(), original);
        await H.$(page, "review-decline").click();
        await H.$(page, "review-preview").waitFor({ state: "detached" });
        assert.deepEqual(await H.head(page, name), before);
        await H.reveal(page, ai);
        await send.click();
        await H.$(page, "ai-answer").filter({ hasText: "Synthetic review proposal" }).waitFor();
        assert.deepEqual(requests, [chatPrompt, editPrompt, editPrompt]);
        await ai.getByRole("button", { name: "Review proposed edits", exact: true }).click();
        await H.$(page, "review-preview").waitFor();
        await H.idle(page);
        await H.accept(page);
        const applied = await H.assertBytes(page, name, Buffer.from(replacement));
        assert.equal(applied.parent, before.id);
        await H.undo(page);
        const undone = await H.assertBytes(page, name, Buffer.from(original));
        assert.equal(undone.parent, applied.id);
        await H.reveal(page, ai);
        await ai.getByRole("button", { name: "Prepare active-file context", exact: true }).click();
        ollamaUnavailable = true;
        await send.click();
        await page.waitForFunction(() => /Cannot reach Ollama|could not be completed/i.test(document.querySelector('[data-testid=ai-status]')?.textContent));
        const fallbackEdit = original + "% Local editing continues.\n";
        await H.$(page, "open-editor").click();
        await H.$(page, "editor").fill(fallbackEdit);
        await H.save(page);
        await H.assertBytes(page, name, Buffer.from(fallbackEdit));
        await H.reveal(page, ai);
        await ai.getByRole("button", { name: "Disconnect and clear memory", exact: true }).click();
        await page.locator('[data-checks-tab="saved"]').click();
        const saved = H.$(page, 'saved-check').filter({ hasText: model });
        await saved.first().waitFor();
        assert.equal(await saved.count(), 3);
        assert.match((await saved.allTextContents()).join(' '), /Synthetic acknowledgement.*|Synthetic review proposal/);
        assert.ok((await saved.allTextContents()).every(text => !text.includes('replacementText') && !text.includes('Acknowledge the synthetic file')));
        await page.reload();
        await H.ready(page);
        await H.open(page, name);
        await H.$(page, 'open-checks').click();
        await page.locator('[data-checks-tab="saved"]').click();
        await saved.first().waitFor();
        assert.equal(await saved.count(), 3);
        await saved.first().locator('summary').click();
        await saved.first().getByRole('button', { name: 'Remove…', exact: true }).click();
        await saved.first().getByRole('button', { name: 'Remove this report', exact: true }).click();
        await page.waitForFunction(() => document.querySelectorAll('[data-check-kind="ai"]').length === 2);
        await H.reveal(page, ai);
        assert.equal(await H.$(page, "ai-context").count(), 0);
        assert.equal(await H.$(page, "ai-answer").count(), 0);
        assert.deepEqual(external, [tags, tags, endpoint, endpoint, endpoint, endpoint]);
        assert.deepEqual(errors, []);
        console.log("Synthetic Ollama UI passed: explicit discovery, manual fallback, disclosed send, retained draft, review, Undo and offline editing.");
    } finally {
        await context.close();
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
