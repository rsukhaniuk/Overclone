// Actual published app with a private loopback origin and synthetic inputs.
// Run in an isolated browser environment with external networking disabled.
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { createHash, randomUUID } = require('node:crypto');
const { once } = require('node:events');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { setTimeout: delay } = require('node:timers/promises');
const { chromium } = require('playwright');
const H = require('./review-ui-helpers.cjs');

const publication = process.env.PUBLISHED_WWWROOT;
const output = process.env.RESULT_DIR;
const channel = process.env.LOCAL_BROWSER_CHANNEL;
const origin = 'http://127.0.0.1:8080';
const sha = value => createHash('sha256').update(value).digest('hex');
const modelMarker = 'synthetic-model-' + randomUUID();
const promptMarker = 'synthetic-instruction-' + randomUUID();
const markers = [modelMarker, promptMarker];
assert.ok(publication && output, 'PUBLISHED_WWWROOT and RESULT_DIR are required');
assert.ok(!channel || ['chrome', 'msedge'].includes(channel), 'Unsupported browser channel');

async function inspectStorage(page) {
    return page.evaluate(async ({ markers, origin }) => {
        const decoder = new TextDecoder();
        function forbidden(value) {
            if (typeof value === 'string') return markers.some(marker => value.includes(marker));
            if (value instanceof ArrayBuffer || ArrayBuffer.isView(value)) return forbidden(decoder.decode(value));
            if (value && typeof value === 'object') return Object.entries(value).some(([key, item]) => forbidden(key) || forbidden(item));
            return false;
        }
        const violations = [];
        for (const [label, storage] of [['localStorage', localStorage], ['sessionStorage', sessionStorage]]) {
            if (forbidden({ ...storage })) violations.push(label);
        }
        let idbRows = 0;
        let opfsEntries = 0;
        function read(request) {
            return new Promise((resolve, reject) => {
                request.onsuccess = () => resolve(request.result);
                request.onerror = () => reject(request.error);
            });
        }
        for (const { name } of await indexedDB.databases()) {
            const db = await read(indexedDB.open(name));
            try {
                for (const store of db.objectStoreNames) {
                    const objects = db.transaction(store).objectStore(store);
                    const [keys, entries] = await Promise.all([
                        read(objects.getAllKeys(undefined, 129)),
                        read(objects.getAll(undefined, 129)),
                    ]);
                    idbRows += entries.length;
                    if (idbRows > 128) throw Error('Synthetic IndexedDB limit');
                    if (forbidden(keys) || forbidden(entries)) violations.push('IndexedDB');
                }
            } finally { db.close(); }
        }
        async function files(dir) {
            for await (const [name, entry] of dir.entries()) {
                if (++opfsEntries > 128) throw Error('Synthetic OPFS limit');
                if (forbidden(name)) violations.push('OPFS name');
                if (entry.kind === 'directory') await files(entry);
                else {
                    const file = await entry.getFile();
                    if (file.size > 1024 * 1024) throw Error('Synthetic OPFS file limit');
                    if (forbidden(await file.arrayBuffer())) violations.push('OPFS content');
                }
            }
        }
        await files(await navigator.storage.getDirectory());
        let cachedRequests = 0;
        for (const name of await caches.keys()) {
            const cache = await caches.open(name);
            for (const { url } of await cache.keys()) {
                cachedRequests++;
                if (new URL(url).origin !== origin || forbidden(url)) violations.push('CacheStorage request');
            }
        }
        return { violations, idbRows, opfsEntries, cachedRequests };
    }, { markers, origin });
}

async function connectSynthetic(page) {
    await H.reveal(page, H.$(page, 'ai-model'));
    await H.$(page, 'ai-model').fill(modelMarker);
    await H.$(page, 'ai-local-url').fill('http://127.0.0.1:11434');
    await H.$(page, 'ai-connect').click();
    await H.$(page, 'ai-destination').waitFor();
    assert.match(await H.$(page, 'ai-destination').textContent(), /http:\/\/127\.0\.0\.1:11434\/api\/chat/);
    const ai = H.$(page, 'ai-panel');
    await ai.getByRole('button', { name: 'Prepare active-file context', exact: true }).click();
    await H.$(page, 'ai-context').waitFor();
    await ai.getByRole('combobox', { name: 'Request type' }).selectOption('Question');
    await ai.locator('textarea').fill(promptMarker);
    return ai;
}

async function reopenExport(page, bytes) {
    await page.getByRole('button', { name: 'Close', exact: true }).click();
    const upload = H.$(page, 'import-zip');
    await H.reveal(page, upload);
    await upload.setInputFiles({ name: 'local-offline-export.zip', mimeType: 'application/zip', buffer: bytes });
    await H.$(page, 'import-preview').waitFor();
    assert.equal(await H.$(page, 'validation-errors').count(), 0);
    assert.equal(await page.locator('.staged-files li').count(), 1);
    const project = 'Local R3 ZIP replay';
    await H.$(page, 'staged-name').fill(project);
    await H.$(page, 'publish-import').click();
    await H.$(page, 'select-file').first().waitFor();
    assert.equal((await H.assertBytes(page, project, H.negative)).files.length, 1);
}

(async () => {
    const profile = await fs.mkdtemp(path.join(os.tmpdir(), 'overclone-r3-local-'));
    const result = { passed: false, scope: 'local-published-app-offline', channel: channel ?? 'chromium', traffic: [], errors: [] };
    let server, browser, phase = 'start';
    async function closeOrigin() {
        const done = once(server, 'exit');
        server.kill();
        await done;
        server = null;
    }
    async function open(offline) {
        browser = await chromium.launchPersistentContext(profile, { headless: true, offline, channel });
        const page = browser.pages()[0];
        page.on('pageerror', () => result.errors.push(phase));
        page.on('request', request => {
            const url = new URL(request.url());
            if (/^https?:$/.test(url.protocol) && url.origin !== origin) result.traffic.push([phase, url.hostname, request.method()]);
        });
        await page.goto(origin);
        await H.ready(page);
        return page;
    }
    try {
        server = spawn(process.execPath, [path.join(__dirname, 'static-server.cjs'), publication, '8080']);
        const [ready] = await Promise.race([
            once(server.stdout, 'data'),
            once(server, 'exit').then(() => { throw Error('Local origin exited during startup'); }),
            delay(5000, [], { ref: false }),
        ]);
        assert.match(String(ready), /static-origin serving/);
        const response = await fetch(origin + '/service-worker-assets.js');
        assert.ok(response.ok);
        const manifest = await response.text();
        result.manifest = { version: manifest.match(/"version"\s*:\s*"([^"]+)"/)?.[1], sha256: sha(manifest) };
        assert.ok(result.manifest.version);
        assert.doesNotMatch(manifest, /google-drive-auth\.js/);
        phase = 'initial-local-work';
        let page = await open(false);
        result.browser = browser.browser().version();
        const project = await H.create(page);
        const saved = await H.assertBytes(page, project, H.negative);
        assert.equal(await page.locator('a[href="#dashboard-drive"]').count(), 0);
        assert.equal(await H.$(page, 'ai-provider').count(), 0);
        assert.equal(await H.$(page, 'ai-key').count(), 0);
        await page.waitForFunction(() => !!navigator.serviceWorker.controller, null, { timeout: 90000 });
        assert.deepEqual(result.traffic, []);
        phase = 'connect-without-send';
        await connectSynthetic(page);
        result.connectedStorage = await inspectStorage(page);
        assert.deepEqual(result.connectedStorage.violations, []);
        assert.deepEqual(result.traffic, []);
        await browser.close();
        browser = null;
        await closeOrigin();
        await assert.rejects(fetch(origin), /fetch failed/);
        result.originRefused = true;
        phase = 'cold-offline';
        page = await open(true);
        assert.equal(await page.evaluate(() => navigator.onLine), false);
        await H.open(page, project);
        await H.reveal(page, H.$(page, 'ai-model'));
        assert.equal(await H.$(page, 'ai-model').inputValue(), '');
        for (const id of ['ai-destination', 'ai-context', 'ai-answer']) assert.equal(await H.$(page, id).count(), 0);
        assert.deepEqual(await H.head(page, project), saved);
        phase = 'disclosed-offline-send';
        const ai = await connectSynthetic(page);
        assert.deepEqual(result.traffic, []);
        await ai.getByRole('button', { name: 'Send disclosed request' }).click();
        await H.$(page, 'ai-status').filter({ hasText: 'Cannot reach Ollama.' }).waitFor();
        assert.equal(await H.$(page, 'ai-answer').count(), 0);
        assert.equal(await H.$(page, 'review-preview').count(), 0);
        await ai.getByRole('button', { name: 'Disconnect and clear memory' }).click();
        await H.$(page, 'ai-context').waitFor({ state: 'detached' });
        assert.deepEqual(await H.head(page, project), saved);
        assert.deepEqual(result.traffic, [['disclosed-offline-send', '127.0.0.1', 'POST']]);
        phase = 'post-failure-local-review';
        await H.selectKnu(page);
        await H.reveal(page, H.$(page, 'preview-fix'));
        await H.preview(page);
        await H.accept(page);
        const fixed = await H.assertBytes(page, project, H.after);
        assert.equal(fixed.parent, saved.id);
        await H.undo(page);
        const undone = await H.assertBytes(page, project, H.negative);
        assert.equal(undone.parent, fixed.id);
        const download = page.waitForEvent('download');
        await H.reveal(page, H.$(page, 'export-zip'));
        await H.$(page, 'export-zip').click();
        const zip = await fs.readFile(await (await download).path());
        assert.ok(markers.every(marker => !zip.includes(marker)));
        await reopenExport(page, zip);
        result.offlineExport = { bytes: zip.length, sha256: sha(zip), exactReimport: true };
        result.offlineStorage = await inspectStorage(page);
        assert.deepEqual(result.offlineStorage.violations, []);
        assert.deepEqual(result.traffic, [['disclosed-offline-send', '127.0.0.1', 'POST']]);
        assert.deepEqual(result.errors, []);
        result.passed = true;
    } catch (error) {
        result.failure = { phase, kind: error.name, message: String(error.message).slice(0, 600) };
        process.exitCode = 1;
    } finally {
        if (browser) await browser.close();
        if (server && server.exitCode === null) await closeOrigin();
        await fs.rm(profile, { recursive: true, force: true });
        await fs.writeFile(path.join(output, 'r3-local-offline-receipt.json'), JSON.stringify(result, null, 2) + '\n');
        console.log(JSON.stringify(result));
    }
})();
