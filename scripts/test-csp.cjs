// Static contract only. Browser enforcement is tested by test-csp-ui.cjs.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

function validate(html, header) {
    const metas = [...html.matchAll(/<meta\b[^>]*>/gi)].filter(m => /http-equiv\s*=\s*["']Content-Security-Policy["']/i.test(m[0]));
    assert.equal(metas.length, 1, 'exactly one enforced meta');
    assert.ok(!/Content-Security-Policy-Report-Only/i.test(html), 'not report-only');
    const meta = metas[0];
    assert.ok(meta.index > html.indexOf('<head>') && meta.index < html.indexOf('</head>'));
    assert.ok(meta.index < html.search(/<(?:base|link|script)\b/i), 'policy before resources/base');
    const policy = /\bcontent="([^"]+)"/.exec(meta[0])?.[1];
    assert.ok(policy);
    const directives = new Map();
    for (const part of policy.split(';').map(p => p.trim()).filter(Boolean)) {
        const [name, ...values] = part.split(/\s+/);
        assert.ok(!directives.has(name), `duplicate directive: ${name}`);
        assert.ok(values.length > 0);
        assert.equal(new Set(values).size, values.length);
        directives.set(name, values);
    }
    const scripts = [...html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)];
    const inline = scripts.filter(m => !/\bsrc\s*=/i.test(m[1]));
    assert.equal(inline.length, 1, 'one unchanged inline startup, not duplicate startup');
    const hash = `'sha256-${crypto.createHash('sha256').update(inline[0][2].replace(/\r\n?/g, '\n'), 'utf8').digest('base64')}'`;
    const expected = {
        'default-src': ["'none'"], 'base-uri': ["'self'"], 'object-src': ["'none'"], 'form-action': ["'none'"],
        'script-src': ["'self'", "'wasm-unsafe-eval'", hash, 'https://accounts.google.com/gsi/client', 'https://apis.google.com'],
        'script-src-attr': ["'none'"], 'worker-src': ["'self'", 'blob:'],
        'style-src': ["'self'"], 'style-src-elem': ["'self'", 'https://accounts.google.com/gsi/style'],
        'style-src-attr': ["'unsafe-inline'"], 'img-src': ["'self'"], 'font-src': ["'self'"],
        'media-src': ["'none'"], 'manifest-src': ["'self'"],
        'connect-src': ["'self'", 'http://localhost:11434', 'http://127.0.0.1:11434',
            'https://generativelanguage.googleapis.com', 'https://api.anthropic.com', 'https://www.googleapis.com',
            'https://accounts.google.com/gsi/', 'https://apis.google.com'],
        'frame-src': ['https://accounts.google.com', 'https://apis.google.com', 'https://docs.google.com'],
    };
    assert.deepEqual([...directives.keys()].sort(), Object.keys(expected).sort(), 'no ignored/extra/overriding directives');
    for (const [name, values] of Object.entries(expected)) {
        assert.deepEqual([...directives.get(name)].sort(), [...values].sort(), name);
    }
    // Exact lists also prohibit unsafe-eval/inline JS, wildcard/scheme-wide HTTP,
    // blob scripts, nonces, strict-dynamic and silent reporting endpoints.
    assert.equal(scripts.length, 2);
    assert.match(scripts.find(m => /\bsrc=/.test(m[1]))[1], /src="_framework\/blazor\.webassembly\.js\?v=v10"/);
    assert.equal((inline[0][2].match(/Blazor\.start\(/g) || []).length, 1);
    const lines = header.split('\n').map(l => l.trim()).filter(l => l.startsWith('Content-Security-Policy:'));
    assert.deepEqual(lines, [`Content-Security-Policy: ${policy}; frame-ancestors 'none'`], 'header mirrors meta exactly, adds header-only embedding defense');
    return policy;
}

if (require.main === module) {
    const html = fs.readFileSync(path.join(__dirname, '../src/Client/wwwroot/index.html'), 'utf8');
    const header = fs.readFileSync(path.join(__dirname, '../docs/examples/static-csp-headers.conf'), 'utf8');
    validate(html, header);
    // Demonstrate that weakening, duplicate-first-wins and bootstrap drift fail.
    for (const bad of [
        html.replace("default-src 'none'", "default-src 'none'; default-src *"),
        html.replace("'wasm-unsafe-eval'", "'unsafe-eval'"),
        html.replace("script-src 'self'", "script-src 'self' https:"),
        html.replace("connect-src 'self'", "connect-src 'self' http: https:"),
        html.replace('let appStartupSucceeded = false;', 'let appStartupSucceeded = true;'),
        html.replace('Content-Security-Policy', 'Content-Security-Policy-Report-Only'),
        html.replace('worker-src', 'child-src'),
    ]) assert.throws(() => validate(bad, header));
    assert.throws(() => validate(html, header.replaceAll("frame-ancestors 'none'", "frame-ancestors *")));
    console.log('CSP static contract passed: actual early enforced meta, exact bootstrap SHA256, bounded directives/provider origins, matching header; 8 negative mutations rejected.');
}
module.exports = { validate };
