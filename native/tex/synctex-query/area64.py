#!/usr/bin/env python3
"""Exact upstream overlay: use 64-bit container areas on wasm32.

No map, query coordinate, candidate-order, ABI/schema or resource bound changes.
"""
import hashlib
from pathlib import Path
import sys

source, out = map(Path, sys.argv[1:])
raw = source.read_bytes()
assert hashlib.sha256(raw).hexdigest() == 'fa8bb24365d1f92c2fe7d75aac0abeda79222d6398737263e8092656c91febc0'
assert not out.exists(), 'fresh overlay required'
text = raw.decode()
start = text.index('SYNCTEX_INLINE static synctex_node_p _synctex_smallest_container_v2(synctex_node_p node, synctex_node_p other_node) {')
end = text.index('\n}\n', start) + 3
before = text[start:end]
after = before.replace('long total_height, other_total_height;', 'int64_t total_height, other_total_height;')
after = after.replace('unsigned long area, other_area;', 'uint64_t area, other_area;')
after = after.replace('long width =', 'int64_t width =').replace('long other_width =', 'int64_t other_width =')
after = after.replace('_synctex_abs(', '_synctex_abs64(')
after = after.replace('area = total_height*width;', 'area = (uint64_t)total_height*(uint64_t)width;')
after = after.replace('other_area = other_total_height*other_width;', 'other_area = (uint64_t)other_total_height*(uint64_t)other_width;')
assert before != after
# Getter dimensions are signed32. Widen before negation/addition: width<=2^31,
# height+depth<=2^32, so their product<=2^63 is exact in uint64, including INT_MIN.
helper = '''SYNCTEX_INLINE static int64_t _synctex_abs64(int value) {
    int64_t wide = value;
    return wide < 0 ? -wide : wide;
}
'''
text = text[:start] + helper + after + text[end:]
assert text.count('#include <limits.h>') == 1
text = text.replace('#include <limits.h>', '#include <limits.h>\n#include <stdint.h>')
out.parent.mkdir(parents=True, exist_ok=True)
out.write_text(text)
