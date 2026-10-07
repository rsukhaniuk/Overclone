import assert from "node:assert/strict";
import createEngine from "/artifacts/wasm/engine.mjs";

const module = await createEngine();
for (const [text, expected] of [["", 0], [" \t\n", 0], ["one two\nthree", 3], ["Привіт світе", 2]]) {
    assert.equal(module.ccall("count_words", "number", ["string"], [text]), expected);
}
console.log("WASM exports and UTF-8 string bridge passed.");
