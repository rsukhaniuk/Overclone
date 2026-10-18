// Published app UI with a synthetic Ollama response. No provider account/key is used.
// This verifies browser wiring and review behavior, not deployed-provider CORS.
const assert = require("node:assert/strict");
const { chromium } = require("playwright");
const H = require("./review-ui-helpers.cjs");
const base = process.env.APP_URL || "http://127.0.0.1:8080";

(async () => {
    const browser = await chromium.launch({ headless: true });
    const context = await browser.newContext({ serviceWorkers: "block" });
    const page = await context.newPage();
    page.setDefaultTimeout(15000);
    const errors = [], external = [], requests = [];
    page.on("pageerror", e => { if (errors.length < 32) errors.push(e.message); });
    page.on("request", r => {
        if (!r.url().startsWith(base + "/") && !r.url().startsWith("data:") && !r.url().startsWith("blob:"))
            external.push(r.url());
    });
    const original = "\\documentclass{article}\n\\begin{document}\nУкраїна🙂\n\\end{document}\n";
    const replacement = original + "% Reviewed AI suggestion🙂\n";
    await page.route("http://localhost:11434/api/chat", async route => {
        const request = route.request();
        const headers = { "access-control-allow-origin": base, "access-control-allow-headers": "*", "access-control-allow-methods": "POST,OPTIONS" };
        if (request.method() === "OPTIONS") { await route.fulfill({ status: 204, headers }); return; }
        assert.equal(request.method(), "POST");
        const body = request.postDataJSON();
        requests.push(body);
        assert.equal(body.model, "ui-test-local");
        assert.equal(body.stream, false);
        assert.equal(body.messages.length, 1);
        await route.fulfill({ headers, contentType: "application/json", body: JSON.stringify({
            done: true, message: { content: JSON.stringify({
                replacementText: replacement, explanation: "Synthetic review proposal"
            }) }
        }) });
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
        await page.getByTestId("ai-disclosure").locator(":scope > summary").click();
        await H.$(page, "ai-provider").selectOption("Gemini");
        await H.$(page, "ai-key").fill("synthetic-unused-ui-key");
        await H.$(page, "ai-model").fill("previous-provider-model");
        await H.$(page, "ai-model").press("Tab"); // Commit the normal onchange binding before selecting another provider.
        await H.$(page, "ai-provider").selectOption("Claude");
        await page.waitForFunction(() => document.querySelector('[data-testid="ai-key"]')?.value === "");
        await page.waitForFunction(() => document.querySelector('[data-testid="ai-model"]')?.value === "");
        assert.equal(await H.$(page, "ai-key").inputValue(), "", "provider switch clears the actual password input");
        assert.equal(await H.$(page, "ai-model").inputValue(), "");
        assert.deepEqual(external, [], "configuration alone sends nothing");
        await H.$(page, "ai-provider").selectOption("Ollama");
        await H.$(page, "ai-model").fill("ui-test-local");
        await H.$(page, "ai-connect").click();
        assert.equal(requests.length, 0);
        await ai.getByRole("button", { name: "Prepare active-file context", exact: true }).click();
        await H.$(page, "ai-context").waitFor();
        assert.equal(await H.$(page, "ai-context").textContent(), original);
        await ai.getByRole("textbox", { name: "Your request", exact: false }).fill("Propose a small correction");
        await ai.getByRole("checkbox", { name: "Request an edit proposal", exact: false }).check();
        await ai.getByRole("button", { name: "Send disclosed request", exact: true }).click();
        await H.$(page, "ai-answer").waitFor();
        assert.equal(requests.length, 1);
        assert.equal(await H.$(page, "editor").inputValue(), original);
        assert.equal(await H.$(page, "review-preview").count(), 0, "reply alone cannot create a review or apply edits");
        await ai.getByRole("button", { name: "Preview proposal in Review (not acceptance)", exact: true }).click();
        await H.$(page, "review-preview").waitFor();
        await H.idle(page);
        assert.equal(await H.$(page, "editor").inputValue(), original);
        await H.accept(page);
        await H.assertBytes(page, name, Buffer.from(replacement));
        await H.undo(page);
        await H.assertBytes(page, name, Buffer.from(original));
        await ai.getByRole("button", { name: "Disconnect and clear memory", exact: true }).click();
        assert.equal(await H.$(page, "ai-context").count(), 0);
        assert.equal(await H.$(page, "ai-answer").count(), 0);
        assert.deepEqual(external, ["http://localhost:11434/api/chat"]);
        assert.deepEqual(errors, []);
        console.log("AI UI passed: no startup requests, provider-key clearing, explicit send, review acceptance, exact undo and disconnect.");
    } finally {
        await context.close();
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
