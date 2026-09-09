#!/usr/bin/env bash
# Build MhwDpsMeter (Release) into dist/Release/nativePC/plugins/CSharp/MhwDpsMeter/
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

DOTNET="${DOTNET:-dotnet}"
if ! command -v "$DOTNET" >/dev/null 2>&1 && [[ "$DOTNET" == dotnet ]]; then
  DOTNET="$HOME/.dotnet/dotnet"
fi

CONFIG="${1:-Release}"
echo "Building $CONFIG with $DOTNET..."
"$DOTNET" build -c "$CONFIG"

OUT="dist/$CONFIG/nativePC/plugins/CSharp/MhwDpsMeter/MhwDpsMeter.dll"
if [[ ! -f "$OUT" ]]; then
  echo "Build output missing: $OUT" >&2
  exit 1
fi

echo "Built: $OUT"
ls -lh "$OUT"
