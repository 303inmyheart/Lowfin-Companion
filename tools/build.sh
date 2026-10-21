#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET_BIN="${DOTNET:-dotnet}"
"$DOTNET_BIN" test "$ROOT/tests/Lowfin.Companion.Tests/Lowfin.Companion.Tests.csproj" -c Release
"$DOTNET_BIN" build "$ROOT/src/Lowfin.Companion/Lowfin.Companion.csproj" -c Release
python3 "$ROOT/tools/package.py"
