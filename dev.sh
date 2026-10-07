#!/bin/sh
set -eu
cd "$(dirname "$0")"
export DEV_UID="$(id -u)"
export DEV_GID="$(id -g)"
mkdir -p src/Client/wwwroot/native-generated src/Client/wwwroot/rust-generated
if [ "$#" -eq 0 ]; then
    set -- up
fi
if [ -f .env ]; then
    exec docker compose --env-file toolchains.env --env-file .env "$@"
fi
exec docker compose --env-file toolchains.env "$@"
