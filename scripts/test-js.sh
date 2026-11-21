#!/bin/sh
set -eu

cd /workspace

# These checks use Node built-ins only. Real browser/PWA acceptance is separate.
node scripts/test-csp.cjs
node scripts/test-bootstrap-config.cjs
for test in tests/browser/*.test.cjs; do
    node "$test"
done
node --experimental-vm-modules tests/Workspace.Tests/SyncTeXTransport/lifecycle.test.cjs
