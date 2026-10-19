// Published UI with synthetic GIS/Picker/Drive responses. No account is contacted.
// Real Google consent, quota and deployed-origin CORS still require separate checks.
const assert = require("node:assert/strict");
const { chromium } = require("playwright");
const { buildZip } = require("./zip-util.cjs");
const H = require("./review-ui-helpers.cjs");
const base = process.env.APP_URL || "http://127.0.0.1:8080";
const token = "synthetic-drive-ui-token";
const scope = "https://www.googleapis.com/auth/drive.file";
const gis = `google = { accounts: { oauth2: { initTokenClient(config) {
    if (config.scope !== ${JSON.stringify(scope)} || config.include_granted_scopes !== false) throw Error('scope');
    return { requestAccessToken() { queueMicrotask(() => config.callback({
        access_token: ${JSON.stringify(token)}, expires_in: 3600, scope: config.scope
    })); } };
} } } };`;
const picker = `gapi = { load(name, options) { options.callback(); } };
google.picker = {
    ViewId: { DOCS: 'docs' }, Action: { CANCEL: 'cancel', PICKED: 'picked' },
    DocsView: class { setIncludeFolders(){return this;} setSelectFolderEnabled(){return this;} setMimeTypes(){return this;} },
    PickerBuilder: class {
        setDeveloperKey(){return this;} setAppId(){return this;} setOAuthToken(){return this;}
        setOrigin(){return this;} addView(){return this;} setCallback(cb){this.callback=cb;return this;}
        build(){const cb=this.callback;return {dispose(){},setVisible(v){if(v)queueMicrotask(()=>cb({action:'picked',docs:[{id:'selected-zip',name:'Imported thesis.zip'}]}));}};}
    }
};`;

(async () => {
    const browser = await chromium.launch({ headless: true });
    const context = await browser.newContext({ serviceWorkers: "block" });
    // Only the synthetic Drive responses are online in the network-isolated runner.
    // This does not establish live connectivity; cold-offline behavior is tested separately.
    await context.addInitScript(() => Object.defineProperty(navigator, "onLine", { get: () => true }));
    const page = await context.newPage();
    page.setDefaultTimeout(15000);
    const errors = [], external = [], calls = [];
    page.on("pageerror", e => { if (errors.length < 32) errors.push(e.message); });
    page.on("request", r => {
        if (!r.url().startsWith(base + "/") && !r.url().startsWith("data:") && !r.url().startsWith("blob:")) external.push(r.url());
    });
    const zip = buildZip([["main.tex", Buffer.from("Imported Ukrainian text🙂\n")]]);
    await page.route("https://accounts.google.com/gsi/client", r => r.fulfill({ contentType: "text/javascript", body: gis }));
    await page.route("https://apis.google.com/js/api.js", r => r.fulfill({ contentType: "text/javascript", body: picker }));
    await page.route("https://www.googleapis.com/**", async route => {
        const request = route.request(), url = new URL(request.url());
        const headers = { "access-control-allow-origin": base, "access-control-allow-headers": "*", "access-control-allow-methods": "GET,POST,OPTIONS" };
        if (request.method() === "OPTIONS") { await route.fulfill({ status: 204, headers }); return; }
        assert.equal(request.headers().authorization, "Bearer " + token);
        assert.ok(!url.href.includes(token));
        calls.push({ method: request.method(), path: url.pathname });
        if (request.method() === "GET") {
            assert.equal(url.pathname, "/drive/v3/files/selected-zip");
            assert.equal(url.searchParams.get("alt"), "media");
            await route.fulfill({ headers, contentType: "application/zip", body: zip });
        } else {
            assert.equal(request.method(), "POST");
            assert.equal(url.pathname, "/upload/drive/v3/files");
            assert.equal(url.searchParams.get("uploadType"), "multipart");
            const bytes = request.postDataBuffer();
            assert.ok(bytes?.includes(Buffer.from("main.tex")));
            assert.ok(!bytes.includes(Buffer.from(token)));
            await route.fulfill({ headers, contentType: "application/json", body: '{"id":"created-new-zip"}' });
        }
    });
    const projects = () => page.evaluate(async () => (await import("./js/workspace-storage.js")).listProjects());
    try {
        await page.goto(base, { waitUntil: "domcontentloaded" });
        await H.ready(page);
        assert.deepEqual(external, [], "no optional Google script or request at startup");
        await H.reveal(page, page.locator("#new-project"));
        await page.locator("#new-project").fill("Current local draft");
        await page.getByRole("button", { name: "Create project", exact: true }).click();
        await H.$(page, "new-file-name").fill("main.tex");
        await H.$(page, "new-file").click();
        const original = "Keep this local source🙂\n";
        await H.$(page, "editor").fill(original);
        const panel = page.getByRole("region", { name: "Optional Google Drive ZIP transfers", exact: true });
        await page.getByTestId("drive-disclosure").locator(":scope > summary").click();
        await H.reveal(page, panel.getByRole("textbox", { name: "OAuth web client ID", exact: true }));
        await panel.getByRole("textbox", { name: "OAuth web client ID", exact: true }).fill("test.apps.googleusercontent.com");
        await panel.getByRole("textbox", { name: "Restricted public Picker API key", exact: true }).fill("public-test");
        await panel.getByRole("textbox", { name: "Cloud project number", exact: false }).fill("12345");
        assert.deepEqual(external, []);
        await panel.getByRole("button", { name: "Connect / reconnect explicitly", exact: true }).click();
        const choose = panel.getByRole("button", { name: "Choose ZIP with Google Picker", exact: true });
        await choose.waitFor();
        try {
            await page.waitForFunction(() => [...document.querySelectorAll('button')].some(b => b.textContent === 'Choose ZIP with Google Picker' && !b.disabled));
        } catch {
            throw new Error(JSON.stringify({ authorization: await panel.locator("p").filter({ hasText: "Authorization:" }).textContent(), notices: await panel.getByRole("status").allTextContents(), errors }));
        }
        assert.equal(calls.length, 0, "connect cannot transfer documents");
        await choose.click();
        const download = panel.getByRole("button", { name: "Download and stage for local review", exact: true });
        await download.waitFor();
        assert.equal(calls.length, 0, "Picker selection cannot start download");
        const before = (await projects()).length;
        await download.click();
        const accept = panel.getByRole("button", { name: "Accept: create NEW local project", exact: true });
        await accept.waitFor();
        assert.equal(calls.length, 1);
        assert.equal((await projects()).length, before, "staging cannot publish");
        await accept.click();
        const deadline = Date.now() + 15000;
        while ((await projects()).length !== before + 1 && Date.now() < deadline)
            await new Promise(resolve => setTimeout(resolve, 100));
        assert.equal((await projects()).length, before + 1, "accepted import publishes exactly one new project");
        assert.equal(await H.$(page, "editor").inputValue(), original, "import preserves the open draft");
        await panel.getByRole("button", { name: "Save locally and preview Drive export", exact: true }).click();
        const upload = panel.getByRole("button", { name: "Confirm: upload NEW ZIP to Drive", exact: true });
        await upload.waitFor();
        assert.equal(calls.length, 1, "export preview cannot upload");
        await upload.click();
        await panel.getByRole("status").filter({ hasText: "created a NEW ZIP" }).waitFor();
        assert.deepEqual(calls.map(c => c.method), ["GET", "POST"]);
        await H.assertBytes(page, "Current local draft", Buffer.from(original));
        await panel.getByRole("button", { name: "Disconnect", exact: true }).click();
        assert.ok(await choose.isDisabled());
        const persisted = await page.evaluate(() => JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage } }));
        assert.ok(!persisted.includes(token));
        assert.deepEqual(errors, []);
        console.log("Drive UI passed: opt-in scripts, narrow consent, explicit staged new-project import, separate confirmed new-file export and disconnect.");
    } finally {
        await context.close();
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
