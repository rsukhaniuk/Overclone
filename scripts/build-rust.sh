#!/bin/sh
set -eu
cargo build --manifest-path /workspace/rust/Cargo.toml --locked --target wasm32-unknown-unknown
# Generate in the build volume first so compiler failures keep the old module.
wasm-bindgen /artifacts/rust/target/wasm32-unknown-unknown/debug/document_engine.wasm \
    --target web --out-dir /artifacts/rust/pkg --out-name document_engine
mkdir -p /artifacts/rust-wasm
cp /artifacts/rust/pkg/document_engine_bg.wasm /artifacts/rust-wasm/
cp /artifacts/rust/pkg/document_engine.js /artifacts/rust-wasm/
