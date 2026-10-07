#!/bin/sh
set -eu

# Pinned renderer + Graphviz/fonts. No public rendering service or host installation.
image='plantuml/plantuml@sha256:0cd265d12d84b8a0cf307a7dd8c74f237d137b9327b5053bd5aa822f38952caa'
docs_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
format=svg
output=/docs/uml/rendered
preview_dir=

case "$#" in
    0) mkdir -p "$docs_dir/uml/rendered" ;;
    2)
        if [ "$1" != '--png' ]; then
            echo 'Usage: sh docs/render-uml.sh [--png /absolute/preview/directory]' >&2
            exit 2
        fi
        case "$2" in
            /*) preview_dir=$2 ;;
            *) echo 'Preview directory must be absolute.' >&2; exit 2 ;;
        esac
        mkdir -p "$preview_dir"
        format=png
        output=/preview
        ;;
    *) echo 'Usage: sh docs/render-uml.sh [--png /absolute/preview/directory]' >&2; exit 2 ;;
esac

set -- docker run --rm --network none --read-only --cap-drop ALL \
    --security-opt no-new-privileges --user "$(id -u):$(id -g)" \
    --tmpfs /tmp:rw,nosuid,nodev,size=128m \
    --env PLANTUML_SECURITY_PROFILE=ALLOWLIST \
    --env plantuml.allowlist.path=/docs/uml \
    --env JAVA_TOOL_OPTIONS=-Djava.awt.headless=true \
    --volume "$docs_dir:/docs" --workdir /docs
if [ -n "$preview_dir" ]; then
    set -- "$@" --volume "$preview_dir:/preview"
fi
"$@" "$image" -charset UTF-8 -nometadata -failfast2 "-t$format" \
    -o "$output" '/docs/uml/*.puml'
if [ "$format" = svg ]; then
    python3 "$docs_dir/check-docs.py" --record-render
fi
