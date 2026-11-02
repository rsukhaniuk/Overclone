#!/bin/bash
# Reuse existing regression assertions with explicit ABI4/ABI5 test admission.
# ABI5 needs the preimage-bound test-abi/test-engine overlay from the handoff.
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
export SUITE_INPUT=${SUITE_INPUT:-/inputs/suite}
export SUITE_PINS_SHA256=${SUITE_PINS_SHA256:-7c611bbd289df4ee072c597014728429ad0edfd851d41832e45a10e334a534f9}
export FORMAT_FILE=${FORMAT_FILE:-/inputs/suite/files/lualatex.fmt}
export RESULTS=${RESULTS:-/control/r2-lua-maps/evidence/abi4-regressions}
export CANDIDATE=${CANDIDATE:-/control/r2-lua-maps/candidate} LUA_ABI=${LUA_ABI:-4}
[[ "$LUA_ABI" == 4 || "$LUA_ABI" == 5 ]] || {
 echo 'unsupported regression ABI'
 exit 1
}
export OUTPUT_NAMES=result-blx.bib,result.bcf,result.run.xml
mkdir -p "$RESULTS"
engine=$here/test-engine.cjs
tests=$here/tests
run() {
 local label=$1
 shift
 printf '%q ' env "$@" node "$engine" latex "$label" >>"$RESULTS/commands.txt"
 printf '\n' >>"$RESULTS/commands.txt"
 env "$@" node "$engine" latex "$label" >"$RESULTS/$label.run.log" 2>&1
 echo "$label exit=0"
}
node "$here/test-abi.cjs" >"$RESULTS/abi.log" 2>&1
node "$here/test-bibliography.cjs" >"$RESULTS/bibliography.log" 2>&1
node "$here/test-paths.cjs" >"$RESULTS/paths.log" 2>&1
run abi4-api-error-revocation SOURCE_FILE="$tests/carry-observer.tex" PROJECT_MANIFEST="$RESULTS/abi3-observer.inputs.json" CARRY_MANIFEST="$RESULTS/abi3-carry.inputs.json" API_ERROR_AFTER_SUCCESS=1
run abi4-confinement SOURCE_FILE="$tests/confinement.tex"
run abi4-include1 SOURCE_FILE="$tests/include.tex" INCLUDE=1 RUNTIME_MANIFEST=/control/font-extra.inputs.json
run abi4-include2 SOURCE_FILE="$tests/include.tex" INCLUDE=1 RUNTIME_MANIFEST=/control/font-extra.inputs.json AUX_FILE="$RESULTS/abi4-include1.aux" CHAPTER_AUX_FILE="$RESULTS/abi4-include1.chapter.aux"
run abi4-named-font SOURCE_FILE="$tests/named-font.tex"
for name in missing-package failure-after-page no-output output-budget; do
 run "abi4-$name" SOURCE_FILE="$tests/$name.tex" EXPECT_FAILURE=1
done
for attack in oversized-write aggregate-growth many-files log-flood stdout-flood mixed-log-flood private-boundary oversized-path seek-write truncate-churn; do
 run "abi4-quota-$attack" SOURCE_FILE="$tests/resource.tex" LUA_PAYLOAD="$tests/$attack.lua" EXPECT_FAILURE=1 CARRY_MANIFEST="$RESULTS/abi3-carry.inputs.json"
done
for lower in mmap msync truncate writefile copy ownership alias mount readonly unlink; do
 run "abi4-hook-$lower" SOURCE_FILE="$tests/carry-observer.tex" PROJECT_MANIFEST="$RESULTS/abi3-observer.inputs.json" CARRY_MANIFEST="$RESULTS/abi3-carry.inputs.json" POST_CASE="$lower"
done
run abi4-shape-refusal SOURCE_FILE="$tests/guard-baseline.tex" TAMPER_VFS_SHAPE=1 EXPECT_FAILURE=1
CANCEL_LABEL=abi4-cancellation node "$here/test-cancellation.cjs" >"$RESULTS/cancellation.log" 2>&1
run abi4-after-cancellation SOURCE_FILE="$tests/named-font.tex"
