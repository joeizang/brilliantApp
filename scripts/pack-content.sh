#!/usr/bin/env bash
# Validate and pack one app's sources; the original invocation still builds DSA.
set -euo pipefail
cd "$(dirname "$0")/.."
APP="${1:-dsa}"
if [[ $# -gt 1 || ! "$APP" =~ ^[a-z][a-z0-9]*(-[a-z0-9]+)*$ || ! -f "apps/$APP/manifest.yaml" ]]; then
  echo "usage: $0 [app] (default: dsa)" >&2
  exit 2
fi
if [[ "$APP" == dsa ]]; then
  dotnet run --project tools/Brilliant.Cli -- pack --app "apps/$APP/manifest.yaml" -o src/Brilliant.App/Resources/Raw/track-1.pack.zip
else
  dotnet run --project tools/Brilliant.Cli -- pack --app "apps/$APP/manifest.yaml"
fi
