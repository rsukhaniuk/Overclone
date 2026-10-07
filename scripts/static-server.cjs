// Tiny zero-dependency static file server for tests. Serves a published
// Blazor WASM wwwroot (the production static build) over plain HTTP, so the
// offline-restart test verifies the PUBLISHED static assets rather than the
// development server. Not production code.

const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");

const root = process.argv[2] || ".";
const port = parseInt(process.argv[3] || "8080", 10);

const TYPES = {
    ".html": "text/html; charset=utf-8",
    ".js": "text/javascript; charset=utf-8",
    ".mjs": "text/javascript; charset=utf-8",
    ".css": "text/css; charset=utf-8",
    ".json": "application/json; charset=utf-8",
    ".wasm": "application/wasm",
    ".png": "image/png",
    ".svg": "image/svg+xml",
    ".ico": "image/x-icon",
    ".txt": "text/plain; charset=utf-8",
    ".dat": "application/octet-stream",
    ".map": "application/json; charset=utf-8",
};

http.createServer((req, res) => {
    let p = decodeURIComponent(new URL(req.url, `http://localhost`).pathname);
    if (p === "/") p = "/index.html";
    const file = path.join(root, p);
    if (!file.startsWith(path.resolve(root))) {
        res.writeHead(403);
        res.end("forbidden");
        return;
    }
    fs.readFile(file, (err, data) => {
        if (err) {
            res.writeHead(404);
            res.end("not found");
            return;
        }
        const ext = path.extname(file).toLowerCase();
        res.writeHead(200, {
            "Content-Type": TYPES[ext] || "application/octet-stream",
            "Cache-Control": "no-cache",
        });
        res.end(data);
    });
}).listen(port, "0.0.0.0", () => {
    console.log(`static-origin serving ${root} on :${port}`);
});
