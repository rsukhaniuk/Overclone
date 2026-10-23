#!/usr/bin/env node
'use strict';
// Focused regression: pdf-preview-canvas.js draw page admission 0..511.
// Uses minimal DOM Canvas doubles for Node unit isolation ONLY; not real browser rendering.
const assert = require('node:assert/strict');
const { createHash } = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'Client', 'wwwroot', 'js', 'pdf-preview-canvas.js'), 'utf8');
const sourceSha = createHash('sha256').update(source).digest('hex');
assert.ok(Buffer.byteLength(source) <= 4096, 'canvas source byte bound');
const records = [];
function test(name, action) { action(); records.push({ name, passed: true }); }
// Minimal Canvas double for Node isolation: stores putImageData input, not real rendering.
function makeCanvas(w = 100, h = 100) {
  const dataset = {};
  const ctx = {
    clearRect(x, y, cw, ch) { ctx._cleared = { x, y, w: cw, h: ch }; },
    putImageData(img, dx, dy) { ctx._image = { data: new Uint8Array(img.data.buffer.slice(0)), width: img.width, height: img.height, dx, dy }; },
  };
  const canvas = Object.create(HTMLCanvasElement.prototype);
  canvas.width = w; canvas.height = h; canvas.dataset = dataset;
  canvas.getContext = (type, opts) => { assert.equal(type, '2d'); assert.ok(opts?.willReadFrequently); return ctx; };
  canvas.parentElement = { parentElement: { clientWidth: 360 } };
  canvas._ctx = ctx;
  return canvas;
}
function rgba(w, h, v = 200) {
  const b = new Uint8Array(w * h * 4);
  for (let i = 0; i < b.length; i += 4) { b[i] = v; b[i+1] = 7; b[i+2] = 3; b[i+3] = 99; }
  return b;
}
const HASH = '0'.repeat(64);
function makeBridge() {
  class HTMLCanvasElement {}
  const mod = { exports: {} };
  const ctx = {
    ImageData: class { constructor(data, w, h) { this.data = data; this.width = w; this.height = h; } },
    Uint8ClampedArray, Uint8Array, HTMLCanvasElement,
    module: mod, exports: mod.exports
  };
  // Transform ESM export to CJS for Node vm isolation.
  const cjsSource = source.replace('export function create', 'function create') + '\nmodule.exports=create;';
  vm.runInContext(cjsSource, vm.createContext(ctx));
  return { create: mod.exports, HTMLCanvasElement };
}
function main() {
  const { create, HTMLCanvasElement } = makeBridge();
  globalThis.HTMLCanvasElement = HTMLCanvasElement;
  test('click captures displayed identity and CSS-relative position before a new frame', () => {
    const c = makeCanvas(); let handler, removed = false; const calls = [];
    c.addEventListener = (kind, fn) => { assert.equal(kind, 'click'); handler = fn; };
    c.removeEventListener = (kind, fn) => { assert.equal(kind, 'click'); assert.equal(fn, handler); removed = true; };
    c.getBoundingClientRect = () => ({ left: 10, top: 20, width: 400, height: 200 });
    const b = create(c, { invokeMethodAsync(...args) { calls.push(args); return Promise.resolve(); } });
    b.invalidate(7); b.draw(7, HASH, 2, 2, 2, rgba(2, 2));
    handler({ button: 0, clientX: 110, clientY: 120, detail: 2 });
    assert.deepEqual(Array.from(calls[0]), ['OnPointer', 7, HASH, 2, .25, .5, true]);
    b.invalidate(8);
    handler({ button: 0, clientX: 110, clientY: 120, detail: 2 });
    assert.equal(calls.length, 1, 'cleared pixels cannot issue another position');
    b.draw(8, '1'.repeat(64), 3, 2, 2, rgba(2, 2));
    assert.equal(calls[0][1], 7, 'already captured click keeps the original generation');
    assert.equal(calls[0][2], HASH, 'already captured click keeps the original PDF');
    handler({ button: 0, clientX: 9, clientY: 120, detail: 1 });
    assert.equal(calls.length, 1, 'outside-canvas clicks are refused');
    b.dispose(); assert.ok(removed);
    handler({ button: 0, clientX: 110, clientY: 120, detail: 2 });
    assert.equal(calls.length, 1, 'saved late listener is inert after disposal');
  });
  test('31 accepted', () => {
    const c = makeCanvas(); const b = create(c);
    assert.ok(b.draw(0, HASH, 31, 2, 2, rgba(2, 2)));
    assert.equal(c.dataset.pdfPage, '31'); assert.equal(c.width, 2); assert.equal(c.height, 2);
  });
  test('32 accepted (was rejected before fix)', () => {
    const c = makeCanvas(); const b = create(c);
    assert.ok(b.draw(0, HASH, 32, 2, 2, rgba(2, 2)));
    assert.equal(c.dataset.pdfPage, '32');
  });
  test('66 accepted', () => {
    const c = makeCanvas(); const b = create(c);
    assert.ok(b.draw(0, HASH, 66, 2, 2, rgba(2, 2)));
    assert.equal(c.dataset.pdfPage, '66');
  });
  test('511 accepted (boundary)', () => {
    const c = makeCanvas(); const b = create(c);
    assert.ok(b.draw(0, HASH, 511, 2, 2, rgba(2, 2)));
    assert.equal(c.dataset.pdfPage, '511');
  });
  test('512 rejected', () => {
    const c = makeCanvas(); const b = create(c);
    assert.throws(() => b.draw(0, HASH, 512, 2, 2, rgba(2, 2)), /Drawing bounds/);
    assert.equal(c.dataset.pdfPage, undefined);
  });
  test('negative rejected', () => {
    const c = makeCanvas(); const b = create(c);
    assert.throws(() => b.draw(0, HASH, -1, 2, 2, rgba(2, 2)), /Drawing bounds/);
  });
  test('fractional rejected', () => {
    const c = makeCanvas(); const b = create(c);
    assert.throws(() => b.draw(0, HASH, 32.5, 2, 2, rgba(2, 2)), /Drawing bounds/);
  });
  test('pixel shape preserved on valid draw', () => {
    const c = makeCanvas(); const b = create(c); const px = rgba(3, 2, 77);
    assert.ok(b.draw(0, HASH, 100, 3, 2, px));
    const img = c._ctx._image;
    assert.equal(img.width, 3); assert.equal(img.height, 2);
    assert.equal(img.data[0], 77); assert.equal(img.data[4], 77);
    assert.equal(img.data[3], 99); assert.equal(c.width, 3); assert.equal(c.height, 2);
    assert.equal(c.dataset.pdfHash, HASH); assert.equal(c.dataset.drawGeneration, '0');
  });
  test('stale generation returns false without drawing', () => {
    const c = makeCanvas(); const b = create(c); b.invalidate(5);
    assert.ok(!b.draw(3, HASH, 50, 2, 2, rgba(2, 2)));
    assert.equal(c.dataset.pdfPage, undefined); assert.equal(c._ctx._image, undefined);
  });
  test('disposed returns false without drawing', () => {
    const c = makeCanvas(); const b = create(c); b.dispose();
    assert.ok(!b.draw(0, HASH, 50, 2, 2, rgba(2, 2)));
    assert.equal(c.width, 0); assert.equal(c.height, 0);
  });
  test('existing shape checks unchanged: bad hash, bad width, bad rgba', () => {
    const c = makeCanvas(); const b = create(c);
    assert.throws(() => b.draw(0, 'bad', 50, 2, 2, rgba(2, 2)), /Drawing bounds/);
    assert.throws(() => b.draw(0, HASH, 50, 0, 2, rgba(2, 2)), /Drawing bounds/);
    assert.throws(() => b.draw(0, HASH, 50, 2, 2, new Uint8Array(3)), /RGBA shape/);
  });
  test('measure unchanged', () => {
    const c = makeCanvas(); const b = create(c);
    assert.equal(b.measure(), 360);
  });
  console.log(JSON.stringify({ checks: records.length, records, sourceSha, scope: 'Node unit isolation with minimal DOM Canvas doubles; not real browser rendering.' }, null, 2));
}
main();
