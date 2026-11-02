#!/usr/bin/env bash
# Finite genuine-native batch; no app composition or publication.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
: "${ENGINE:?actual Xe maps candidate required}"
: "${ENGINE_FORMAT:?actual compatible new Xe format required}"
: "${CONVERTER:?actual ABI3 XDV7 converter required}"
: "${RESULTS:?fresh evidence directory required}"
[[ ! -e "$RESULTS" ]] || {
 echo 'refuse existing batch'
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
for mode in positive mag1200 changed inline-include bounds off default-off cap invalid-config repeat-config failure quota api-error post-config bibtex-after-success read-traversal write-resource pages512 pages513 bibtex format unsafe noninvertible bbox-program media-override dict-injection rotate-name crop-name pages40000; do
 run "engine-$mode" env RESULTS="$RESULTS/runtime" node "$here/test-runtime.cjs" "$mode"
done
cap=$(
 python3 - "$RESULTS/runtime/positive.synctex" <<'PY'
from pathlib import Path
import sys
b=Path(sys.argv[1]).read_bytes()
print(b.index(b'Postamble:')+len(b'Postamble:'))
PY
)
run engine-postamble env RESULTS="$RESULTS/runtime" MAP_CAP="$cap" node "$here/test-runtime.cjs" postamble
for mode in positive mag1200 changed inline-include bounds unsafe noninvertible bbox-program media-override dict-injection rotate-name crop-name pages512 pages513; do
 run "converter-$mode" env RESULTS="$RESULTS/converted" XDV_INPUT="$RESULTS/runtime/$mode.xdv" node "$here/test-convert.cjs" "$mode"
done
for mode in off default-off invalid-config repeat-config post-config api-error empty bad-header truncated bad-postamble unknown-unit missing-resources mutate-caller-buffer; do
 run "converter-$mode" env RESULTS="$RESULTS/converted" XDV_INPUT="$RESULTS/runtime/positive.xdv" node "$here/test-convert.cjs" "$mode"
done
run oracle env RESULTS="$RESULTS/converted" ENGINE_RESULTS="$RESULTS/runtime" ORACLE_CASES='positive mag1200 changed inline-include' python3 "$here/oracle.py"
run config-query env RESULTS="$RESULTS/config-query" MAP_FILE="$RESULTS/runtime/positive.synctex" node "$here/test-config-query.cjs"
clock="$here/../dvi/fixed-clock.cjs"
run compat-old env TZ=UTC REFERENCE=1 RESULTS="$RESULTS/compat/old" node --require "$clock" "$here/test-compat.cjs"
# New node/string format ABI is explicit: retain exact legacy/new-format log
# differences rather than filtering them. New on/off/default must match fully.
run compat-new-on env TZ=UTC MAPS=1 COMPARE_TO="$RESULTS/compat/old" RECORD_ABI_DIFFERENCE=1 RESULTS="$RESULTS/compat/new-on" node --require "$clock" "$here/test-compat.cjs"
for mode in 0 default; do
 run "compat-new-$mode" env TZ=UTC MAPS="$mode" COMPARE_TO="$RESULTS/compat/new-on" RESULTS="$RESULTS/compat/new-$mode" node --require "$clock" "$here/test-compat.cjs"
done
if [[ -n ${XDV_RESOURCES:-} ]]; then
 run cancel env RESULTS="$RESULTS/cancellation" CAP_XDV="$RESULTS/runtime/pages40000.xdv" RECOVERY_XDV="$RESULTS/runtime/positive.xdv" node "$here/test-cancel.cjs"
fi
