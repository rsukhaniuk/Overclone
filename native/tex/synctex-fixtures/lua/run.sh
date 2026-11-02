#!/bin/bash
# One owned native evidence batch. No downloads, resource/profile writes or UI.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=${RESULTS:-/control/r2-lua-maps/evidence/final}
export CANDIDATE=${CANDIDATE:-/control/r2-lua-maps/candidate}
[[ ! -e "$root" ]] || { echo 'refuse existing final evidence directory'; exit 1; }
mkdir -p "$root/runtime"
run() {
 local log=$1; shift
 printf '%q ' "$@" >> "$root/commands.txt"; printf '\n' >> "$root/commands.txt"
 "$@" > "$root/$log.log" 2>&1
 printf '%s exit=0\n' "$log" | tee -a "$root/exits.txt"
}
for cell in positive changed unicode off default-off cap invalid-config repeat-config native-fail pdf-fail quota post-config post-invalid-root post-budget carry-off forbidden-carry; do
 run "$cell" env RESULTS="$root/runtime" node "$here/test-runtime.cjs" "$cell"
done
run carry-on env RESULTS="$root/runtime" PASS_TAG=402 node "$here/test-runtime.cjs" carry-on
cap=$(($(wc -c < "$root/runtime/positive.synctex") - 6))
run postamble env RESULTS="$root/runtime" MAP_CAP="$cap" node "$here/test-runtime.cjs" postamble
run config env RESULTS="$root" node "$here/test-config.cjs"
run oracle env RESULTS="$root" python3 "$here/oracle.py"
run cancel env RESULTS="$root" node "$here/test-cancel.cjs"
run compat-old env CANDIDATE=/control/master-inputs/profile7-lua LUA_ABI=3 RESULTS="$root/compat-old" node "$here/test-compat.cjs"
run compat-new env RESULTS="$root/compat-new" COMPARE_TO="$root/compat-old" node "$here/test-compat.cjs"
run abi4-regressions env RESULTS="$root/abi4-regressions" bash "$here/../../luatex-engine/synctex/regressions.sh"
