#!/bin/bash
# One coherent real native batch after freezing source; no app/browser adoption.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${ENGINE:?actual separate DVI producer directory required}"
: "${CONVERTER:?actual separate ABI2 converter directory required}"
: "${RESULTS:?fresh evidence directory required}"
[[ ! -e "$RESULTS" ]] || {
 echo 'refuse existing batch evidence'
 exit 1
}
mkdir -p "$RESULTS/runtime" "$RESULTS/converted"
run() {
 local label=$1
 shift
 printf '%q ' "$@" >>"$RESULTS/commands.txt"
 printf '\n' >>"$RESULTS/commands.txt"
 "$@" >"$RESULTS/$label.log" 2>&1
 echo "$label exit=0"
}
for mode in positive mag1200 changed inline-include bounds off default-off cap invalid-config repeat-config failure quota api-error post-config bibtex-after-success read-traversal write-resource pages512 pages513 bibtex format unsafe noninvertible bbox-program media-override dict-injection rotate-name crop-name; do
 run "engine-$mode" env RESULTS="$RESULTS/runtime" node "$here/test-runtime.cjs" "$mode"
done
cap=$(
 python3 - "$RESULTS/runtime/positive.synctex" <<'PY'
from pathlib import Path
import sys
b=Path(sys.argv[1]).read_bytes()
i=b.index(b'Postamble:')
assert i+8<len(b)
print(i+8)
PY
)
run engine-postamble env MAP_CAP="$cap" RESULTS="$RESULTS/runtime" node "$here/test-runtime.cjs" postamble
for mode in positive mag1200 changed inline-include bounds unsafe noninvertible bbox-program media-override dict-injection rotate-name crop-name pages512 pages513; do
 run "converter-$mode" env DVI_INPUT="$RESULTS/runtime/$mode.dvi" RESULTS="$RESULTS/converted" node "$here/test-convert.cjs" "$mode"
done
for mode in off default-off invalid-config repeat-config post-config api-error empty bad-header truncated bad-postamble unknown-unit missing-resources mutate-caller-buffer; do
 run "converter-$mode" env DVI_INPUT="$RESULTS/runtime/positive.dvi" RESULTS="$RESULTS/converted" node "$here/test-convert.cjs" "$mode"
done
run oracle env RESULTS="$RESULTS/converted" ENGINE_RESULTS="$RESULTS/runtime" ORACLE_CASES='positive mag1200 changed inline-include' python3 "$here/oracle.py"
run config-query env MAP_FILE="$RESULTS/runtime/positive.synctex" RESULTS="$RESULTS/config-query" node "$here/test-config-query.cjs"
for mode in pdf dvi; do
 run "compat-$mode-reference" env TZ=UTC REFERENCE=1 RESULTS="$RESULTS/compat/$mode-reference" node --require "$here/fixed-clock.cjs" "$here/test-compat.cjs" "$mode"
 run "compat-$mode-new" env TZ=UTC RESULTS="$RESULTS/compat/$mode-new" COMPARE_TO="$RESULTS/compat/$mode-reference" node --require "$here/fixed-clock.cjs" "$here/test-compat.cjs" "$mode"
 run "compat-$mode-reference-off" env TZ=UTC REFERENCE=1 MAPS=0 RESULTS="$RESULTS/compat/$mode-reference-off" node --require "$here/fixed-clock.cjs" "$here/test-compat.cjs" "$mode"
 run "compat-$mode-new-off" env TZ=UTC MAPS=0 RESULTS="$RESULTS/compat/$mode-new-off" COMPARE_TO="$RESULTS/compat/$mode-reference-off" node --require "$here/fixed-clock.cjs" "$here/test-compat.cjs" "$mode"
done
if [[ -n "${CAP_DVI:-}" && -n "${DVI_RESOURCES:-}" ]]; then
 run cancel env RECOVERY_DVI="$RESULTS/runtime/positive.dvi" RESULTS="$RESULTS/cancellation" node "$here/test-cancel.cjs"
fi
