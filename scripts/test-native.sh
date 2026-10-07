#!/bin/sh
set -eu
# Host-native tests execute inside the Emscripten container, not on the host.
cmake -S /workspace/native -B /artifacts/native/tests -DCMAKE_BUILD_TYPE=Debug
cmake --build /artifacts/native/tests --parallel 2
ctest --test-dir /artifacts/native/tests --output-on-failure
node /workspace/scripts/test-wasm.mjs
