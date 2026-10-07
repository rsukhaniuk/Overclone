#!/bin/sh
set -eu
cargo fmt --manifest-path /workspace/rust/Cargo.toml -- --check
cargo test --manifest-path /workspace/rust/Cargo.toml --locked
cargo clippy --manifest-path /workspace/rust/Cargo.toml --locked --target wasm32-unknown-unknown -- -D warnings
