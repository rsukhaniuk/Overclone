'use strict';
// Native QA. Establish actual PDF glyph and page facts before consulting the
// captured transform and map from the same sealed compilation.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createHash } = require('node:crypto');

const [project, renderer, resources, capture, result] = process.argv.slice(2);
const root = 'тези/my thesis.tex';
const included = 'глави/chapter one.tex';
const e1Pins = [
    'ea2cebb93f554788751e58554b6e16a094ee6d0883774cb697cbb4a5ce8ec38c',
    'b1630e6501e7715b4e0870a059f4e9b8b5acc4c96aa2af0e4ecacb04071622d7',
];
const queryPins = [
    '42f9467bb0d03dc488decab6baec00c2a78f4f524dc18a5a1b912ca55cdcaafd',
    '974212d6edbdad96e9733d854e427883a7b16fc09202e8468baa9d3cdda410b8',
];
const media = [[12,18,612,818], [-24,-30,576,770], [-18,24,582,824], [30,-18,630,782]];
const names = ['alpha', 'beta', 'gamma', 'delta'];
const read = (directory, name) => fs.readFileSync(path.join(directory, name));
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const pin = bytes => ({ length: bytes.length, sha256: sha(bytes) });

function nativeModule(directory, js, wasm, expected) {
    const loader = read(directory, js), binary = read(directory, wasm);
    assert.deepEqual([sha(loader), sha(binary)], expected);
    return require(path.resolve(directory, js))({ wasmBinary: binary, print() {}, printErr() {} });
}

function markerLine(file, phrase) {
    const lines = read(project, file).toString('utf8').split('\n');
    const occurrences = lines.flatMap((line, index) => line.includes(phrase) ? [index + 1] : []);
    assert.equal(occurrences.length, 1, phrase);
    return occurrences[0];
}

function transform([a,b,c,d,e,f], [x,y]) {
    return [a*x+c*y+e, b*x+d*y+f];
}

function inverse([a,b,c,d,e,f], [x,y]) {
    const determinant = a*d-b*c;
    assert.ok(Number.isFinite(determinant) && determinant !== 0);
    return [(d*(x-e)-c*(y-f))/determinant, (-b*(x-e)+a*(y-f))/determinant];
}

function bounds(matrix, [l,b,r,t]) {
    const points = [l,r].flatMap(x => [b,t].map(y => transform(matrix, [x,y])));
    return [Math.min(...points.map(p => p[0])), Math.min(...points.map(p => p[1])),
        Math.max(...points.map(p => p[0])), Math.max(...points.map(p => p[1]))];
}

function matchesSource(native, source) {
    return native.line === source.line &&
        [source.file, './' + source.file, '/work/' + source.file].includes(native.file);
}

async function observePdf(pdf) {
    const e = await nativeModule(renderer, 'e1.js', 'e1.wasm', e1Pins);
    const execute = (name, ...arguments_) => assert.equal(e['_' + name](...arguments_), 0, name);
    const input = bytes => {
        const handle = e._e1_alloc(bytes.length);
        assert.ok(handle);
        e.HEAPU8.set(bytes, e._e1_data(handle));
        return handle;
    };
    const take = () => {
        const resultHandle = e._e1_result(), size = e._e1_size(resultHandle);
        assert.ok(size > 0);
        const buffer = input(Buffer.alloc(size));
        execute('e1_copy', resultHandle, buffer);
        const bytes = Buffer.from(e.HEAPU8.slice(e._e1_data(buffer), e._e1_data(buffer) + size));
        execute('e1_release', buffer);
        execute('e1_release', resultHandle);
        return bytes;
    };
    const budget = Buffer.alloc(36);
    [1,36,8388608,32,100000,256,4000000,8388608,64].forEach((value, index) => budget.writeUInt32LE(value, index*4));
    const pdfInput = input(pdf), budgetInput = input(budget);
    execute('e1_open', pdfInput, budgetInput);
    const document = e._e1_result();
    execute('e1_release', pdfInput);
    execute('e1_release', budgetInput);
    assert.equal(e._e1_document_pages(document), 4);

    const pages = [];
    for (const [index, word] of names.entries()) {
        execute('e1_page', document, index);
        const page = e._e1_result();
        execute('e1_facts', page);
        const factsBytes = take(), facts = JSON.parse(factsBytes);
        execute('e1_text', page);
        const textBytes = take(), text = JSON.parse(textBytes);
        execute('e1_close', page);
        fs.writeFileSync(path.join(result, `page-${index + 1}-facts.json`), factsBytes);
        fs.writeFileSync(path.join(result, `page-${index + 1}-text.json`), textBytes);

        assert.deepEqual(facts.media, media[index]);
        assert.deepEqual(facts.effective, media[index].map((edge, n) => edge + (n < 2 ? 5 : -5)));
        assert.equal(facts.rotation, index * 90);
        assert.equal(facts.cropDefaulted, false);
        const glyphs = [[root, `Toplevel ${word} roundtrip`], [included, `Included ${word} anchor`],
            [root, `Far edge ${word}`]].map(([file, phrase]) => {
            const start = text.text.indexOf(phrase);
            assert.ok(start >= 0, phrase);
            assert.equal(text.text.indexOf(phrase, start + 1), -1, phrase);
            const glyph = text.characters.find(ch => ch.index === start);
            assert.equal(glyph.boxStatus, 'available');
            const [l,b,r,t] = glyph.box;
            assert.ok(l < r && b < t);
            return { file, line: markerLine(file, phrase), phrase, glyph, rawPoint: [(l+r)/2,(b+t)/2] };
        });
        pages.push({ index, facts, factsSha256: sha(factsBytes), textSha256: sha(textBytes), glyphs });
    }
    execute('e1_close', document);
    assert.equal(e._e1_live(), 0);
    return pages;
}

async function run() {
    fs.mkdirSync(result);
    const pdf = read(capture, 'output.pdf'), pages = await observePdf(pdf);
    const map = read(capture, 'raw.synctex'), geometry = read(capture, 'geometry.json');
    const native = JSON.parse(read(capture, 'receipt.json')), sidecar = JSON.parse(geometry);
    assert.deepEqual(pin(pdf), native.outputs.pdf);
    assert.deepEqual(pin(map), native.rawMap);
    assert.equal(sha(geometry), native.geometry.sha256);
    assert.deepEqual(Object.keys(sidecar), ['schemaVersion','pages']);
    assert.equal(sidecar.schemaVersion, 1);
    assert.equal(sidecar.pages.length, pages.length);

    const q = await nativeModule(resources, 'synctex-query.js', 'synctex-query.wasm', queryPins);
    assert.equal(q._sq_area64_abi(), 1);
    const put = bytes => {
        const pointer = q._malloc(bytes.length);
        assert.ok(pointer);
        q.HEAPU8.set(bytes, pointer);
        return pointer;
    };
    const mapPointer = put(map);
    assert.equal(q._sq_load(mapPointer, map.length), 0);
    q._free(mapPointer);
    function reply(code, label) {
        assert.equal(code, 0, label);
        const length = q._sq_result_length();
        assert.ok(length > 0 && length <= 32768);
        const pointer = q._malloc(length);
        assert.ok(pointer);
        assert.equal(q._sq_result_copy(pointer, length), 0);
        const bytes = Buffer.from(q.HEAPU8.slice(pointer, pointer + length));
        q._free(pointer);
        const response = JSON.parse(bytes);
        assert.equal(response.truncated, false);
        assert.ok(response.matches.length > 0, label);
        fs.writeFileSync(path.join(result, label + '.json'), bytes);
        return { best: response.matches[0], sha256: sha(bytes) };
    }
    let guessedPlacementMisses = 0;
    const observations = [];
    for (const page of pages) {
        const pageNumber = page.index + 1, record = sidecar.pages[page.index];
        assert.equal(record.page, pageNumber);
        assert.equal(record.status, 'available');
        assert.equal(record.reason, 'none');
        const matrix = record.writerToPdfRaw;
        const measured = bounds(matrix, record.writerPageBoxBp);
        measured.forEach((edge, n) => assert.ok(Math.abs(edge - page.facts.media[n]) < 0.01));
        for (const [index, glyph] of page.glyphs.entries()) {
            const key = `page-${pageNumber}-anchor-${index + 1}`;
            const writerPoint = inverse(matrix, glyph.rawPoint);
            const {best: inverseMatch, sha256: inverseSha256} = reply(q._sq_inverse(pageNumber, ...writerPoint), key + '-inverse');
            assert.ok(matchesSource(inverseMatch, glyph), `${glyph.phrase} inverse expected ${glyph.file}:${glyph.line}, got ${inverseMatch.file}:${inverseMatch.line}`);
            const file = Buffer.from(glyph.file), pointer = put(file);
            const forward = reply(q._sq_forward(pointer, file.length, glyph.line, 1, -1), key + '-forward');
            q._free(pointer);
            assert.equal(forward.best.page, pageNumber, key);
            const {h,v,width,height,depth} = forward.best.box;
            const box = [h,v-height,h+width,v+depth];
            const contains = ([l,b,r,t]) => {
                const [x,y] = glyph.rawPoint;
                return x >= l - .01 && x <= r + .01 && y >= b - .01 && y <= t + .01;
            };
            assert.ok(contains(bounds(matrix, box)), `${glyph.phrase} forward box must contain E1 glyph center`);
            if (!contains(bounds([1,0,0,-1,0,page.facts.media[3]], box))) guessedPlacementMisses++;
            observations.push({ page: pageNumber, ...glyph, writerPoint, inverseMatch, inverseSha256, forward });
        }
    }
    assert.equal(q._sq_close(), 0);
    assert.ok(guessedPlacementMisses > 0, 'fixture distinguishes the guessed MediaBox top from the native origin');
    fs.writeFileSync(path.join(result, 'receipt.json'), JSON.stringify({
        status: 'PASS', pdf: pin(pdf), map: pin(map), geometry: pin(geometry), e1Pins, queryPins,
        guessedPlacementMisses, pages, observations
    }, null, 2) + '\n');
    console.log(`${observations.length} exact forward/inverse source locations across four actual PDF rotations`);
}

run().catch(error => { console.error(error); process.exitCode = 1; });
