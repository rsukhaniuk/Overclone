#!/bin/sh
set -eu
cd "$(dirname "$0")"

usage() {
    cat <<'EOF'
Usage: ./verify.sh [quick|managed|js|native|rust]
  quick    Portable managed suite and Node contract checks (default).
  managed  Solution build and portable managed suite; reports fixture skips.
  js       Node contract checks; no npm install, browsers or app server.
  native   Build C++ WASM, then native and WASM smoke tests.
  rust     Formatting, locked unit tests, WASM clippy and bindings build.

Uses existing Docker toolchains and build caches without starting watchers.
Build images once with: ./dev.sh --profile test build app rust js-test
Quick is developer feedback, not compiler, live-service or offline-PWA acceptance.
Do not run concurrently with watchers/builds sharing the artifact volumes.
EOF
}

mode=${1:-quick}
if [ "$#" -gt 1 ]; then usage >&2; exit 2; fi
case "$mode" in
    -h|--help) usage; exit 0 ;;
    quick|managed|js|native|rust) ;;
    *) usage >&2; exit 2 ;;
esac

managed() {
    # These four classes require externally provisioned bytes and otherwise
    # throw. Other fixture-aware tests retain their explicit xUnit skip reports.
    filter='FullyQualifiedName!~Workspace.Tests.CompilationNative.NativePortTests&FullyQualifiedName!~Workspace.Tests.CompilationNative.InstalledNativeProviderTests&FullyQualifiedName!~Workspace.Tests.CompilationNative.ModernNativeFixtureTests&FullyQualifiedName!~Workspace.Tests.CompilationNative.AuthoringNativeFixtureTests'
    printf '%s\n' 'Portable suite: four external-runtime classes excluded; fixture skips are not passes.'
    ./dev.sh run --rm --no-deps -T app dotnet build Workspace.slnx -warnaserror
    ./dev.sh run --rm --no-deps -T app dotnet test tests/Workspace.Tests/Workspace.Tests.csproj --no-build --no-restore --filter "$filter"
}

js() { ./dev.sh --profile test run --rm --no-deps -T js-test; }

# The existing init service prepares permissions on dedicated cache volumes.
# JS checks use a read-only checkout and do not need those volumes.
if [ "$mode" != js ]; then
    ./dev.sh run --rm --no-deps -T init
fi
case "$mode" in
    quick) managed; js ;;
    managed) managed ;;
    js) js ;;
    native) ./dev.sh run --rm --no-deps -T native sh -ec 'sh scripts/build-native.sh; sh scripts/test-native.sh' ;;
    rust) ./dev.sh run --rm --no-deps -T rust sh -ec 'sh scripts/test-rust.sh; sh scripts/build-rust.sh' ;;
esac
