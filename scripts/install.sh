#!/usr/bin/env bash
# Install a built MhwDpsMeter into the Monster Hunter World game root.
# Usage:
#   scripts/install.sh [game-root]
#   MHW_DIR=/path/to/game scripts/install.sh
# Defaults to the Steam library path used by scripts/watch-live-debug.sh.
# Preserves settings.json and logs/ in the destination plugin folder.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

CONFIG="${CONFIG:-Release}"
SRC="dist/$CONFIG/nativePC/plugins/CSharp/MhwDpsMeter"
DEFAULT_GAME="/mnt/steam/SteamLibrary/steamapps/common/Monster Hunter World"
GAME="${1:-${MHW_DIR:-$DEFAULT_GAME}}"

if [[ ! -f "$SRC/MhwDpsMeter.dll" ]]; then
  echo "No build at $SRC/MhwDpsMeter.dll — run scripts/build.sh first." >&2
  exit 1
fi

if [[ ! -d "$GAME" ]]; then
  echo "Game root not found: $GAME" >&2
  echo "Pass the folder that contains MonsterHunterWorld.exe, or set MHW_DIR." >&2
  exit 1
fi

DEST="$GAME/nativePC/plugins/CSharp/MhwDpsMeter"
mkdir -p "$DEST/Addresses"

cp "$SRC/MhwDpsMeter.dll" "$DEST/"
[[ -f "$SRC/MhwDpsMeter.pdb" ]] && cp "$SRC/MhwDpsMeter.pdb" "$DEST/"
[[ -f "$SRC/MhwDpsMeter.deps.json" ]] && cp "$SRC/MhwDpsMeter.deps.json" "$DEST/"
cp "$SRC/Addresses/"*.map "$DEST/Addresses/"

echo "Installed into $DEST:"
ls -lh "$DEST/MhwDpsMeter.dll" "$DEST/Addresses/"*.map
echo ""
echo "Quit the game fully and relaunch so SharpPluginLoader picks up the new DLL."
