#!/bin/bash
# One affected real-WASM batch; native-only, never app/browser acceptance.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${CANDIDATE:?explicit ABI5 candidate required}"
: "${RESULTS:?explicit fresh geometry evidence directory required}"
[[ ! -e "$RESULTS" ]] || {
 echo 'refuse existing batch evidence'
 exit 1
}
mkdir -p "$RESULTS/runtime" "$RESULTS/geometry"
export LUA_EXPECTED_ABI=5
run() {
 local label=$1
 shift
 printf '%q ' "$@" >>"$RESULTS/commands.txt"
 printf '\n' >>"$RESULTS/commands.txt"
 "$@" >"$RESULTS/$label.log" 2>&1
 echo "$label exit=0"
}
run config env RESULTS="$RESULTS" node "$here/test-config.cjs"
for mode in positive changed unicode off default-off cap invalid-config repeat-config native-fail pdf-fail quota post-config post-invalid-root post-budget carry-off forbidden-carry; do
 run "runtime-$mode" env RESULTS="$RESULTS/runtime" node "$here/test-runtime.cjs" "$mode"
done
run runtime-carry-on env PASS_TAG=402 RESULTS="$RESULTS/runtime" node "$here/test-runtime.cjs" carry-on
# Choose a genuine overflowing postamble cap from this exact native map.
cap=$(
 python3 - "$RESULTS/runtime/positive.synctex" <<'PY'
from pathlib import Path
import sys
b = Path(sys.argv[1]).read_bytes()
i = b.index(b'Postamble:')
assert i + 8 < len(b)
print(i + 8)
PY
)
run runtime-postamble env MAP_CAP="$cap" RESULTS="$RESULTS/runtime" node "$here/test-runtime.cjs" postamble
for mode in controls fallback valid-delimiters rotate-prefix rotate-signed-prefix crop-prefix name-escape numeric-junk attribute-duplicate literal lua-print lua-literal setmatrix colorstack changed-height attributes duplicate-media lua-attributes tree-attributes reorder raw-object tex-object form direction pages512 pages513; do
 run "geometry-$mode" env RESULTS="$RESULTS/geometry" node "$here/test-geometry.cjs" "$mode"
done
run geometry-oracle env RESULTS="$RESULTS/geometry" python3 "$here/geometry-oracle.py"
run legacy-map-oracle env RESULTS="$RESULTS" python3 "$here/oracle.py"
run cancel env RESULTS="$RESULTS" node "$here/test-cancel.cjs"
