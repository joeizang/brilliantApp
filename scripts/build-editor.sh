#!/usr/bin/env bash
# Rebuilds the offline CodeMirror bundle (src/Brilliant.Lessons.UI/wwwroot/code-editor.js) from
# src/Brilliant.Lessons.UI/editor/src. The bundle is committed, so this is only needed after editing the editor
# source or its dependencies.
set -euo pipefail
cd "$(dirname "$0")/../src/Brilliant.Lessons.UI/editor"
npm ci
npm run build
