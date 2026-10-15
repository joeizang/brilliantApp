#!/usr/bin/env bash
# Build one app flavor: scripts/build-app.sh <app> <mac|android> [Debug|Release]
set -euo pipefail
cd "$(dirname "$0")/.."

if [[ $# -lt 2 || $# -gt 3 ]]; then
  echo "usage: $0 <app> <mac|android> [Debug|Release]" >&2
  exit 2
fi
APP="$1"
PLATFORM="$2"
CONFIGURATION="${3:-Debug}"
if [[ ! "$APP" =~ ^[a-z][a-z0-9]*(-[a-z0-9]+)*$ ]] || [[ ! -f "apps/$APP/manifest.yaml" ]]; then
  echo "Unknown app flavor: $APP" >&2
  exit 2
fi
if [[ "$CONFIGURATION" != Debug && "$CONFIGURATION" != Release ]]; then
  echo "Configuration must be Debug or Release." >&2
  exit 2
fi
case "$PLATFORM" in
  mac) FRAMEWORK="net10.0-maccatalyst" ;;
  android) FRAMEWORK="net10.0-android" ;;
  *) echo "Platform must be mac or android." >&2; exit 2 ;;
esac

if [[ "$PLATFORM" == android && "$CONFIGURATION" == Release ]]; then
  exec scripts/build-apk.sh --app "$APP"
fi
dotnet build src/Brilliant.App -f "$FRAMEWORK" -c "$CONFIGURATION" -p:BrilliantApp="$APP" --nologo
echo "Build output: src/Brilliant.App/bin/$APP/$CONFIGURATION/$FRAMEWORK/"
