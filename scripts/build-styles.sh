#!/usr/bin/env bash
# Rebuilds the Tailwind utilities (src/Brilliant.Lessons.UI/wwwroot/tailwind.css) from src/Brilliant.Lessons.UI/styles/tailwind.css,
# scanning the .razor files for the classes they use. The output is committed, so this is only needed after editing
# tailwind.css or adding a utility class to a .razor file.
#   scripts/build-styles.sh           rebuild
#   scripts/build-styles.sh --check   rebuild into a temp file and fail if the committed output is stale
set -euo pipefail
cd "$(dirname "$0")/../src/Brilliant.Lessons.UI/styles"
npm ci --silent
if [[ "${1:-}" == "--check" ]]; then
    tmp="$(mktemp)"
    trap 'rm -f "$tmp"' EXIT
    npx tailwindcss -i tailwind.css -o "$tmp" --minify >/dev/null 2>&1
    if ! cmp -s "$tmp" ../wwwroot/tailwind.css; then
        echo "wwwroot/tailwind.css is out of date: run scripts/build-styles.sh and commit the result." >&2
        exit 1
    fi
    echo "wwwroot/tailwind.css is up to date."
else
    npm run build
fi
