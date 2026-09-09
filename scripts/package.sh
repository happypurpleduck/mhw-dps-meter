#!/usr/bin/env bash
# Package MhwDpsMeter for Windows (and Linux – same DLL) as a game-root zip.
# Output: dist/MhwDpsMeter-<version>.zip containing:
#   nativePC/plugins/CSharp/MhwDpsMeter/MhwDpsMeter.dll
#   nativePC/plugins/CSharp/MhwDpsMeter/Addresses/*.map
# Extract this zip into the folder that contains MonsterHunterWorld.exe.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

VERSION="$(grep -oP '<Version>\K[^<]+' src/MhwDpsMeter/MhwDpsMeter.csproj | head -1)"
if [[ -z "$VERSION" ]]; then
  echo "Could not read <Version> from src/MhwDpsMeter/MhwDpsMeter.csproj" >&2
  exit 1
fi

DOTNET="${DOTNET:-dotnet}"
if ! command -v "$DOTNET" >/dev/null 2>&1 && [[ "$DOTNET" == dotnet ]]; then
  DOTNET="$HOME/.dotnet/dotnet"
fi

echo "Building Release ($VERSION) with $DOTNET..."
"$DOTNET" build -c Release

SRC="dist/Release/nativePC/plugins/CSharp/MhwDpsMeter"
if [[ ! -f "$SRC/MhwDpsMeter.dll" ]]; then
  echo "Build output missing: $SRC/MhwDpsMeter.dll" >&2
  exit 1
fi

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

mkdir -p "$STAGE/nativePC/plugins/CSharp/MhwDpsMeter/Addresses"
cp "$SRC/MhwDpsMeter.dll" "$STAGE/nativePC/plugins/CSharp/MhwDpsMeter/"
cp "$SRC/Addresses/"*.map "$STAGE/nativePC/plugins/CSharp/MhwDpsMeter/Addresses/"

OUT="dist/MhwDpsMeter-${VERSION}.zip"
OUT_WIN="dist/MhwDpsMeter-${VERSION}-windows.zip"
rm -f "$OUT" "$OUT_WIN"

( cd "$STAGE" && zip -r "$ROOT/$OUT" nativePC >/dev/null )
cp "$OUT" "$OUT_WIN"

echo "Created:"
ls -lh "$OUT" "$OUT_WIN"
echo "Contents:"
unzip -l "$OUT"
echo ""
echo "Extract into the game root (beside MonsterHunterWorld.exe):"
echo "  unzip -o $OUT -d \"<MHW game dir>\""
