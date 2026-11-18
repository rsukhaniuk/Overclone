'use strict';
// Genuine native QA only. Do not rewrite source/fonts/maps or replace native
// functions. Native Unicode/nominal boxes establish correlation, not glyph ink.
const fs = require('node:fs'), path = require('node:path'), assert = require('node:assert/strict');
const { createHash } = require('node:crypto');
const [base, pdfRuntime, queryDir] = process.argv.slice(2);
const hash = b => createHash('sha256').update(b).digest('hex');
(async () => {
    const loader = fs.readFileSync(path.join(pdfRuntime, 'e1.js')), wasm = fs.readFileSync(path.join(pdfRuntime, 'e1.wasm'));
    assert.equal(hash(loader), '968aae4935523f528e2314c4dbd831b2dd326ac8d3de3444fd033b73463b0225');
    assert.equal(hash(wasm), '8b715ed561ec644757f305ddad0a99ae728c6705f9868e96ccb53c8c9143c4b3');
    const e = await require(path.join(pdfRuntime, 'e1.js'))({ wasmBinary: wasm, print() {}, printErr() {} });
    const ok = (name, ...a) => assert.equal(e['_' + name](...a), 0, name);
    const input = b => { const h = e._e1_alloc(b.length); assert.ok(h); e.HEAPU8.set(b, e._e1_data(h)); return h; };
    const take = () => {
        const h = e._e1_result(), n = e._e1_size(h); assert.ok(n > 0 && n <= 8388608);
        const d = input(Buffer.alloc(n)); ok('e1_copy', h, d);
        const b = Buffer.from(e.HEAPU8.slice(e._e1_data(d), e._e1_data(d) + n));
        ok('e1_release', d); ok('e1_release', h); return JSON.parse(b);
    };
    const pdf = fs.readFileSync(path.join(base, 'native01/output.pdf'));
    const map = fs.readFileSync(path.join(base, 'native01/raw.synctex'));
    const budget = Buffer.alloc(36);
    [1, 36, 8388608, 512, 100000, 256, 4000000, 8388608, 64].forEach((n, i) => budget.writeUInt32LE(n, i * 4));
    const d = input(pdf), b = input(budget); ok('e1_open', d, b); const doc = e._e1_result();
    ok('e1_release', d); ok('e1_release', b); const pages = [];
    for (let i = 0; i < e._e1_document_pages(doc); i++) {
        ok('e1_page', doc, i); const page = e._e1_result();
        ok('e1_facts', page); const facts = take(); ok('e1_text', page); const text = take();
        pages.push({ facts, text }); ok('e1_close', page);
    }
    ok('e1_close', doc); assert.equal(e._e1_live(), 0);
    // Preserve actual observations even if a strict native query oracle fails.
    const receipt = { schemaVersion: 1, status: 'recording-native-evidence',
        scope: 'Original synthetic Ukrainian root plus actually executed included file; actual final-E1v2 text/nominal-box and native-map correlation only. No issued source role or glyph-ink proof.',
        pdf: { length: pdf.length, sha256: hash(pdf) }, map: { length: map.length, sha256: hash(map) },
        pdfRuntime: { loader: hash(loader), wasm: hash(wasm) },
        rustResponse: hash(fs.readFileSync(path.join(base, 'response.json'))), pages, rows: [] };
    const write = () => fs.writeFileSync(path.join(base, 'native-fixture.json'), JSON.stringify(receipt, null, 2) + '\n');
    write();
    const qloader = fs.readFileSync(path.join(queryDir, 'synctex-query.js'));
    const qwasm = fs.readFileSync(path.join(queryDir, 'synctex-query.wasm'));
    assert.equal(hash(qloader), '42f9467bb0d03dc488decab6baec00c2a78f4f524dc18a5a1b912ca55cdcaafd');
    assert.equal(hash(qwasm), '974212d6edbdad96e9733d854e427883a7b16fc09202e8468baa9d3cdda410b8');
    receipt.query = { loader: hash(qloader), wasm: hash(qwasm) };
    const q = await require(path.join(queryDir, 'synctex-query.js'))({ noInitialRun: true,
        instantiateWasm(imports, done) { const w = new WebAssembly.Instance(new WebAssembly.Module(qwasm), imports); done(w); return w.exports; } });
    assert.equal(q._sq_area64_abi(), 1);
    const put = b => { const p = q._malloc(b.length || 1); assert.ok(p); q.HEAPU8.set(b, p); return p; };
    let p = put(map); assert.equal(q._sq_load(p, map.length), 0); q._free(p);
    const result = () => {
        const n = q._sq_result_length(); assert.ok(n > 0 && n <= 32768); const p = q._malloc(n);
        assert.equal(q._sq_result_copy(p, n), 0); const b = Buffer.from(q.HEAPU8.slice(p, p + n)); q._free(p);
        const r = JSON.parse(b); assert.equal(r.truncated, false); return r;
    };
    const response = JSON.parse(fs.readFileSync(path.join(base, 'response.json')));
    const request = JSON.parse(fs.readFileSync(path.join(base, 'request.json')));
    const events = response.payload.thesis.events.filter(e => e.kind === 'captionTitle'); assert.equal(events.length, 2);
    for (const event of events) {
        const file = request.payload.files.find(f => f.fileId === event.fileId).relativePath;
        const source = fs.readFileSync(path.join(base, 'project', file));
        assert.equal(source.subarray(event.span.startByte, event.span.endByte).toString('utf8'), event.text);
        const line = source.subarray(0, event.span.startByte).toString('utf8').split('\r\n').length;
        const literal = event.text; assert.match(literal, /І/); assert.match(literal, /і/);
        const name = Buffer.from(file); p = put(name);
        assert.equal(q._sq_forward(p, name.length, line, 1, -1), 0); q._free(p); const forward = result();
        const hits = pages.flatMap((pg, index) => {
            const scalars = pg.text.characters.map(c => String.fromCodePoint(c.unicode)); const all = scalars.join('');
            const at = all.indexOf(literal); if (at < 0) return [];
            assert.equal(all.indexOf(literal, at + 1), -1);
            const start = [...all.slice(0, at)].length;
            return [{ pg, page: index + 1, start, end: start + [...literal].length }];
        });
        assert.equal(hits.length, 1, `exact original native text: ${literal}`);
        const hit = hits[0]; assert.equal(hit.pg.facts.media[0], 0); assert.equal(hit.pg.facts.media[1], 0);
        const row = { literal, file, line, page: hit.page, start: hit.start, end: hit.end, sourceSpan: event.span, forward, inverses: [] };
        receipt.rows.push(row); write();
        assert.ok(forward.matches.length > 0); const bounds = forward.matches[0].box;
        assert.equal(forward.matches[0].line, line); assert.equal(forward.matches[0].page, hit.page);
        const chars = hit.pg.text.characters.slice(hit.start, hit.end).filter(c => !/^\s$/u.test(String.fromCodePoint(c.unicode)));
        for (const c of chars) {
            assert.equal(c.boxStatus, 'available'); const box = c.box;
            const h = (box[0] + box[2]) / 2, v = hit.pg.facts.media[3] - (box[1] + box[3]) / 2;
            assert.ok(h >= bounds.h && h <= bounds.h + bounds.width && v >= bounds.v - bounds.height && v <= bounds.v + bounds.depth,
                `native forward box contains original scalar U+${c.unicode.toString(16)}`);
        }
        for (const c of [chars[0], chars.at(-1)]) {
            const box = c.box, h = (box[0] + box[2]) / 2, v = hit.pg.facts.media[3] - (box[1] + box[3]) / 2;
            assert.equal(q._sq_inverse(hit.page, h, v), 0); const reply = result(); row.inverses.push({ page: hit.page, h, v, reply }); write();
            assert.ok(reply.matches.length > 0); assert.equal(reply.matches[0].line, line);
            assert.ok([file, './' + file, '/work/' + file].includes(reply.matches[0].file), 'exact native source filename');
        }
    }
    assert.equal(q._sq_close(), 0); receipt.status = 'actual-native-correlation-pass-source-role-still-unsupported'; write();
    console.log(JSON.stringify({ status: receipt.status, pdf: receipt.pdf, map: receipt.map,
        rows: receipt.rows.map(({ literal, file, line, start, end }) => ({ literal, file, line, start, end })) }));
})().catch(error => { console.error(error); process.exitCode = 1; });
