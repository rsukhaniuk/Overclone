// Real Chromium geometry/selection/scheduling checks with synthetic native glyph
// boxes. Real compiler/PDF coverage lives in test-editor-pdf-reader-ui.cjs.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { chromium } = require('playwright');
const root = path.resolve(__dirname, '..');
const source = fs.readFileSync(process.env.PDF_READER_SOURCE || path.join(root, 'src/Client/wwwroot/js/pdf-reader.js'), 'utf8');
const css = fs.readFileSync(path.join(root, 'src/Client/wwwroot/app.css'), 'utf8');
(async () => {
    const server = http.createServer((q, r) => r.end('<!doctype html><style>' + css + '\n.pdf-reader{width:600px;height:500px;overflow:auto}.pdf-reader .pdf-sheet{width:600px;position:relative}</style><div class="pdf-reader" tabindex="0"></div>'));
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const origin = `http://127.0.0.1:${server.address().port}`;
    const browser = await chromium.launch();
    try {
        const context = await browser.newContext({ viewport: { width: 900, height: 800 } });
        await context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin });
        const page = await context.newPage();
        await page.goto(origin);
        await page.evaluate(async source => {
            const { createReader } = await import(URL.createObjectURL(new Blob([source], { type: 'text/javascript' })));
            window.calls = [];
            window.hash = 'a'.repeat(64);
            window.reader = createReader(document.querySelector('.pdf-reader'), {
                invokeMethodAsync(...args) { calls.push({ args, at: performance.now() }); return Promise.resolve(); }
            });
            reader.configure(hash, 12, 0, 100);
            reader.invalidate(1);
            const glyphs = [];
            function text(value, start, top) {
                let x = start;
                for (const char of value) {
                    glyphs.push({text:char, box:{left:x/600, top:top/800, width:8/600, height:14/800}});
                    x += 8;
                }
            }
            text('Earlier paragraph', 40, 50);
            glyphs.push({text:'\r',box:null},{text:'\n',box:null});
            text('Primary use case', 40, 100);
            glyphs.push({text:' ', box:null});
            text('Enterprise', 260, 100);
            glyphs.push({text:'\r', box:null}, {text:'\n', box:null});
            text('Україна 😀 text', 40, 150);
            glyphs.push({text:'\r', box:null}, {text:'\n', box:null});
            text('Next line', 40, 190);
            reader.draw(1, hash, 0, 600, 800, new Uint8Array(600*800*4), {glyphs,links:[],rotation:0});
        }, source);
        const view = page.locator('.pdf-reader');
        const box = await view.boundingBox();
        async function drag(x1, y1, x2, y2) {
            await page.mouse.move(box.x+x1, box.y+y1); await page.mouse.down();
            await page.mouse.move(box.x+x2, box.y+y2, {steps:15}); await page.mouse.up();
            await page.keyboard.press('Control+c');
            return page.evaluate(() => navigator.clipboard.readText());
        }
        const cell = await drag(39, 107, 169, 107);
        console.log('Table cell clipboard:', JSON.stringify(cell));
        assert.equal(cell, 'Primary use case', 'selection must follow the actual table cell glyph positions');
        assert.equal(await drag(169,107,39,107), 'Primary use case', 'backward drag');
        assert.equal(await drag(41,107,167,107), 'Primary use case', 'drag beginning directly on text');
        await page.mouse.dblclick(box.x+60,box.y+107);
        assert.equal(await page.evaluate(() => getSelection().toString()), 'Primary', 'double-click selects a whole word across positioned glyphs');
        assert.equal(await drag(39,157,153,157), 'Україна 😀 text', 'Unicode text and supplementary scalar');
        assert.match((await drag(20,157,115,197)).replace(/\s+/g,' '), /Україна 😀 text Next line/, 'multiline margin drag');
        await page.evaluate(() => { window.started = performance.now(); for(let i=0;i<20;i++) setTimeout(() => {document.querySelector('.pdf-reader').scrollTop = 900+i%2*2;},i*25); });
        await page.waitForTimeout(450);
        const calls = await page.evaluate(() => calls.filter(call => call.args[0] === 'OnReaderPage').map(call => call.at-started));
        assert.ok(calls.length && calls[0] < 200, `continuous scroll must request a page before it stops: ${calls}`);
        assert.equal(await page.evaluate(() => reader.draw(1,hash,1,1,1,new Uint8Array(4),{glyphs:[],links:[]})), true, 'neighbor can paint without changing current page');
        assert.equal(await page.evaluate(() => reader.draw(0,hash,2,1,1,new Uint8Array(4),null)), false, 'stale neighbor refused');
        assert.equal(await page.evaluate(() => reader.draw(1,'b'.repeat(64),2,1,1,new Uint8Array(4),null)), false, 'foreign document refused');
        await page.evaluate(() => reader.dispose());
        assert.equal(await page.evaluate(() => reader.draw(1,hash,0,1,1,new Uint8Array(4),null)), false, 'disposed reader refused');
        console.log('PASS table cell, backward, Unicode, multiline copy; continuous scroll and neighbor freshness');
    } finally { await browser.close(); server.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
