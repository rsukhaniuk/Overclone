#!/usr/bin/env python3
"""Run the existing54 origin-domain assertions on current semantic/HB lineages.

Only select the current native FFI fixture; all original negative assertions and
source vectors remain exact. No geometry/coordinate/result substitution.
"""
import hashlib
from pathlib import Path

here = Path(__file__).resolve().parent
parent = here / 'test-domains.py'
text = parent.read_text()
assert hashlib.sha256(parent.read_bytes()).hexdigest() == 'bd1d9f40328b66bec6a90d39807853091973a9b7906e0827221bb7ee140546fa'
assert text.count("here/'run-project.cjs'") == 2
text = text.replace("here/'run-project.cjs'", "here/'run-current-project.cjs'")
exec(compile(text, str(parent), 'exec'), {'__name__': '__main__', '__file__': str(parent)})
