# Synthetic Rust → C# source-analysis fixtures

These are **small synthetic LaTeX inputs**, not user theses, rendered PDFs or
compliance certification. Each JSON file records `sourceHex` (the exact complete
UTF-8 bytes), `sourceSha256`, the canonical `request`, and the actual Rust public
entry-point `response`. None is a hand-written alternative response schema.

The initial five fixtures were generated on 2026-10-29 during Assignment 3a from
the independently accepted, corrected Assignment 2 implementation. The producer was a temporary native Rust
crate outside the repository, importing `document-engine` at `/workspace/rust`
and invoking **`document_engine::analyze_source`**. It was compiled with the
existing pinned Docker Rust service, offline from the provisioned Cargo cache:

```sh
./dev.sh exec -T rust cargo build --offline \
  --manifest-path /tmp/oop-m2b-client-fixtures/Cargo.toml \
  --target-dir /tmp/oop-m2b-client-fixtures/target
```

The temporary crate had `document-engine = { path = "/workspace/rust" }` and
`serde_json = "1"`. Its resolved runtime package versions **and checksums** were
compared by `(name, version)` against `rust/Cargo.lock` (including both `syn`
versions) and matched. The repository lockfile/dependencies were not changed.
Native binaries, target directories and the temporary generator are not shipped.

The complete temporary generator entry was:

```rust
use std::io::{self, BufRead};
fn main() {
    for line in io::stdin().lock().lines() {
        let v: serde_json::Value = serde_json::from_str(&line.unwrap()).unwrap();
        let bytes: Vec<u8> = serde_json::from_value(v["packed"].clone()).unwrap();
        println!("{}", document_engine::analyze_source(
            &v["request"].to_string(), &bytes));
    }
}
```

A host Python driver encoded the synthetic input as UTF-8, computed SHA-256 with
`hashlib.sha256`, supplied `{request, packed: list(bytes)}` on standard input via
`docker exec -i oop-dev-rust-1 /tmp/oop-m2b-client-fixtures/target/debug/m2b-client-fixtures`,
parsed the returned JSON, and wrote the fixtures without altering response fields.
The engine fixture uses the engine-only profile and omits configuration. The initial
four KNU fixtures use the four explicit work types, not inferred filename choices.
Each request's working generation is **9007199254740993**, above JavaScript's
safe integer range, to exercise exact integer JSON transport.

| Fixture | Input bytes | Work type / outcome | SHA-256 of exact source bytes |
| --- | --- | --- | --- |
| positive.json | 80 | unspecified / Pass | `690e5de6f944d87fb50bc37905802399313a4b9811decf5df6d7bfa6e8442b99` |
| negative.json | 77 | bachelorQualification / Fail, one F1 | `a673f8ebcbb51619544922e95e5ff8de6eb326dc0f51aba43c63f12cf06d5f02` |
| uncertain.json | 49 | masterQualification / Indeterminate | `59accc3d0ba8b54401351254219e362c91dd1530aa76f687d7780813820b9e93` |
| unavailable.json | 29 | coursework / Unavailable | `670c0c47bf23f3773006ccac1ec010dc4d8aaa7be5dd41fd0bef9d10083e96bb` |
| engine.json | 25 | engine-only / Pass | `d266034617ba1db3e8f98a4f08fa8676efcde6e9f115ef0c1cf451e49580448d` |

Positive/negative inputs include Ukrainian, emoji, a retained BOM and CRLF/lone
CR/LF. Positive additionally contains a combining mark and a heading whose final
scalar is 🙂 (two UTF-16 code units). Negative contains the literal `10 mm`
indent and a terminal heading period. Uncertain contains a macro definition in
an owned indent value followed by `Good`, exercising the accepted common
uncertainty correction. Unavailable is plain Ukrainian text without observations.
Inspect `sourceHex` to reconstruct the exact bytes rather than normalizing these
descriptions or relying on a Markdown renderer's newline handling.

Producer source identity: SHA-256
`bf9cb10c340f53692c425ad7ad161bc4107f9496b4db63000df761c49619b5ee`
over the 12 sorted paths comprising `rust/Cargo.toml`, `rust/Cargo.lock`, and all
`rust/src/**/*.rs` except `*_tests.rs`. For each file the digest input is its
repository-relative path in UTF-8, NUL, exact file bytes, NUL. This records the
actual uncommitted accepted source, rather than falsely tying it to an older HEAD.

To reproduce using a temporary crate with the entry above, feed each fixture's
stored request and `list(bytes.fromhex(sourceHex))` into that executable and
compare the parsed response objects. Verify the source hash and descriptor
byteLength before comparing. Do not regenerate goldens automatically on test
failure; review protocol changes against the accepted Rust contract first.

`src/Client/Analysis/knu-profile-metadata.json` is an exact semantic copy of the
positive response's `payload.knu.metadata`, embedded in the client assembly as a
finite validation catalog. It is offline data, not a second rule evaluator and
not a runtime guideline download. Tests validate the known identity/metadata
against incoming reports.

`KnuProtocolTests` uses original fixtures for full round trips and adapter
correlation, plus explicitly tampered copies for rejection tests. Workflow tests
use `KnuFixtures.Reply` to rebind transport identities/configuration to real
immutable workspace captures while retaining Rust source/fact/span data.
The engine-only scheduling projection and fault/held-await responses are labeled
synthetic test doubles, not newly claimed Rust/browser execution. Those original
3a tests execute no proposal. Later 4b service tests explicitly execute the native
F1 bytes through the accepted patch/review seams, as described below; no browser
review/apply claim is implied.

## Argument-bound correction fixture

`argument-bounds.json` was generated on 2026-10-30 with the **same** temporary
public-entry producer, rebuilt with `cargo build --locked --offline` using the
same manifest/target paths above. Accepted Rust source was not changed. A separate
Python driver reused the positive request's identities/configuration, replaced
its packed source, and updated hasBom (false), byteLength and source SHA-256.
The returned response object was saved unchanged. It is an actual native
**Indeterminate** report, with known 4096-byte controls alongside unknown
4097-byte arguments, not a hand-constructed oversized-known report.

- Complete source length: **24662 UTF-8 bytes**; workType: unspecified.
- Source SHA-256: `ede5c60f06fae330ee9fafda864d55bc2e5ea3848df338416991495d68a55cb6`.
- Fixture JSON SHA-256: `0d50ce409d405a83a116b2992cffe01cd819ee61e98787be4ff13a9b2e1476c0`.
- Producer source/lock identity remains the 12-file digest recorded above.

For exact reproduction, define these strings for `n = 4096` or `4097`:

```python
def target(n): return ' ' * (n - 10) + r'\parindent'
def value(n): return '1.27' + ' ' * (n - 6) + 'cm'
def heading(n): return 'У' * 2046 + ('x' if n == 4097 else '') + '🙂'
```

Concatenate, each followed by LF: setlength with target(4096)/value(4096), section
with heading(4096), setlength with target(4097)/`1.27cm`, setlength with
`\parindent`/value(4097), section with heading(4097). Wrap each mandatory argument
in ordinary braces. Each constructed argument has exactly n **UTF-8 bytes**;
the headings have 2048/2049 UTF-16 units respectively. `sourceHex` is the complete
byte-level reference, including spaces and LF.

The producer emits five observations: known indent (4096-byte value **and** raw
target), known heading (4096 bytes), unknown indent with a 4097-byte target,
unknown indent with a 4097-byte value, and unknown heading with a 4097-byte value.
The latter three retain null known length/scalar and explicit unsupportedArgument
uncertainty; the file also carries argumentTooLong. Excerpts remain scalar-safe
and at most 256 bytes; no F1 is emitted. Global Indeterminate does not erase the
first two known-value controls.

`KnuArgumentBoundsTests` accepts this unchanged report through both public paths,
then uses explicit adversarial copies to forge known-value claims on its three
4097-byte observations. It also reproduces the lead's 5020-byte claimed heading
by extending positive.json's captured bytes, facts, value/declaration spans and
bounded excerpt. Those forged copies are negative tests, not new Rust goldens.
Known values must obey the producer's derivation bound; unknown spans are **not**
unconditionally capped at 4096 bytes.

## Review reanalysis control (Assignment 4b)

`review-after.json` is a new actual native response after the exact eligible F1
replacement in negative.json: replace only ASCII `10 mm` with `1.27cm`, preserving
all other BOM/CRLF/lone-CR/LF/Ukrainian/emoji bytes. It remains bachelorQualification;
the heading period remains, but paragraph-indent observation passes and no F1
is offered. This is an idempotence control, not a whole-document Pass or a newly
invented heading fix.

- Source: **78 bytes**, SHA-256
  `9ebdf2e05f9374dafbcb285005c136a4de6be671baa8910b61338e3c1fcd21d0`.
- Fixture JSON SHA-256:
  `1897a8bb1cfd206db559c6b5cff51a2fc1653e321ce79de4c94c15ae858125f3`.
- Same 12-file Rust producer/lock digest above; no Rust source/dependency changes.

Reproduction: copy negative.json's request, replace those source bytes and update
only file byteLength/source SHA-256. Feed that request and packed bytes to the
same temporary public-entry producer, rebuilt using `cargo build --locked
--offline --manifest-path /tmp/oop-m2b-client-fixtures/Cargo.toml --target-dir
/tmp/oop-m2b-client-fixtures/target` in the Rust container. Python inside the native
container computes the input hash and packages the returned JSON object (preserving
integers, including generation 9007199254740993); no JS JSON/number round-trip or
rule evaluation. Final fresh native output was compared structurally with the
stored response and matched exactly. Original goldens are not regenerated.

`ChangeReviewServiceTests` routes these real report objects through the real
RustSourceAnalyzer/strict adapter and current AnalysisCoordinator. Identity rebinding
uses the existing fixture helper. Generic multi-file proposals and epoch/storage/
lock/fault barriers are explicitly synthetic around the real C# service; they
are not browser review UI acceptance.
