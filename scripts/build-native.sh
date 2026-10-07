#!/bin/sh
set -eu
emcmake cmake -S /workspace/native -B /artifacts/native/wasm -DCMAKE_BUILD_TYPE=Debug
cmake --build /artifacts/native/wasm --parallel 2
mkdir -p /artifacts/wasm
cp /artifacts/native/wasm/engine.wasm /artifacts/wasm/engine.wasm
cp /artifacts/native/wasm/engine.mjs /artifacts/wasm/engine.mjs
