#!/usr/bin/env bash
# Validates ./content and builds the Content Pack that the Mac app bundles.
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet run --project tools/Brilliant.Cli -- pack content -o src/Brilliant.App/Resources/Raw/track-1.pack.zip
