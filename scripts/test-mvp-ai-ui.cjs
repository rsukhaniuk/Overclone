// Published app UI with synthetic Ollama responses. Run with external networking disabled.
const assert = require("node:assert/strict");
const { chromium } = require("playwright");
const H = require("./review-ui-helpers.cjs");
const base = process.env.APP_URL || "http://127.0.0.1:8080";
const origin = new URL(base).origin;
const model = "ui-test-local";
const endpoint = "http://localhost:11434/api/chat";

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
    await page.route(endpoint, async route => {
        const request = route.request();
        const headers = { "access-control-allow-origin": origin, "access-control-allow-headers": "content-type", "access-control-allow-methods": "POST,OPTIONS", "cache-control": "no-store" };
        if (request.method() === "OPTIONS") { await route.fulfill({ status: 204, headers }); return; }
        assert.equal(request.method(), "POST");
        const reply = mockResponse(request, requests.length === 0 ? chat : proposal);
        requests.push(reply.prompt);
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
        // The optional section may be collapsed to keep the editor uncluttered.
        await H.reveal(page, H.$(page, "ai-model"));
        assert.equal(await H.$(page, "ai-provider").count(), 0);
        assert.equal(await H.$(page, "ai-key").count(), 0);
        await H.$(page, "ai-model").fill(model);
        await H.$(page, "ai-connect").click();
        await H.$(page, "ai-destination").waitFor();
        assert.match(await H.$(page, "ai-destination").textContent(), /Ollama/);
        assert.equal(requests.length, 0);
        await ai.getByRole("button", { name: "Prepare active-file context", exact: true }).click();
        await H.$(page, "ai-context").waitFor();
        assert.equal(await H.$(page, "ai-context").textContent(), original);
        const before = await H.head(page, name);
        const instruction = ai.getByRole("textbox", { name: "Your request", exact: false });
        const send = ai.getByRole("button", { name: "Send disclosed request", exact: true });
        await instruction.fill("Acknowledge the synthetic file");
        const chatPrompt = await H.$(page, "ai-payload").textContent();
        await send.click();
        await H.$(page, "ai-answer").filter({ hasText: chat }).waitFor();
        assert.deepEqual(requests, [chatPrompt]);
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
        await ai.getByRole("button", { name: "Preview proposal in Review (not acceptance)", exact: true }).click();
        await H.$(page, "review-preview").waitFor();
        await H.idle(page);
        assert.equal(await H.$(page, "editor").inputValue(), original);
        await H.accept(page);
        const applied = await H.assertBytes(page, name, Buffer.from(replacement));
        assert.equal(applied.parent, before.id);
        await H.undo(page);
        const undone = await H.assertBytes(page, name, Buffer.from(original));
        assert.equal(undone.parent, applied.id);
        await ai.getByRole("button", { name: "Disconnect and clear memory", exact: true }).click();
        assert.equal(await H.$(page, "ai-context").count(), 0);
        assert.equal(await H.$(page, "ai-answer").count(), 0);
        assert.deepEqual(external, [endpoint, endpoint]);
        assert.deepEqual(errors, []);
        console.log("Synthetic Ollama UI passed: disclosed chat and edit, reviewed save, exact Undo and disconnect.");
    } finally {
        await context.close();
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
