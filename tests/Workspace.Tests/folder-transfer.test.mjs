import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";

const source = await readFile(
    new URL("../../src/Client/wwwroot/js/folder-transfer.js", import.meta.url),
    "utf8",
);
const transport = await import(`data:text/javascript;base64,${Buffer.from(source).toString("base64")}`);

function directory(...entries) {
    return {
        async *values() {
            yield* entries;
        },
    };
}

function file(name, getFile = async () => new Blob([Uint8Array.of(42)])) {
    return { kind: "file", name, getFile };
}

test("failed enumeration releases its registered file handles only", async () => {
    const { files } = await transport.enumerateTree(directory(file("other.tex")));
    const otherId = files[0].handleId;
    const unavailable = file("unavailable.tex", async () => {
        throw new Error("Permission revoked");
    });

    await assert.rejects(
        transport.enumerateTree(directory(file("registered.tex"), unavailable)),
        /Permission revoked/,
    );
    await assert.rejects(transport.readFileHandleById(otherId + 1), /Unknown file handle/);
    assert.deepEqual(await transport.readFileHandleById(otherId), Uint8Array.of(42));
    await assert.rejects(transport.readFileHandleById(otherId), /Unknown file handle/);
});

test("failed browser read consumes its registered handle", async () => {
    let opens = 0;
    const entry = file("broken.tex", async () => {
        opens++;
        return {
            size: 1,
            async arrayBuffer() {
                throw new Error("Could not read file");
            },
        };
    });

    const { files } = await transport.enumerateTree(directory(entry));
    await assert.rejects(transport.readFileHandleById(files[0].handleId), /Could not read file/);
    await assert.rejects(transport.readFileHandleById(files[0].handleId), /Unknown file handle/);
    assert.equal(opens, 2);
});
