#!/usr/bin/env bash
# Builds the browser version: wasm32 with the nightly toolchain (gpui_web's parking_lot
# needs it), then wasm-bindgen into www/src/wasm, then copies the sample logs.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

MODE="${1:---release}"
case "$MODE" in
  --release) PROFILE=release; FLAGS=--release ;;
  --dev|--debug) PROFILE=debug; FLAGS= ;;
  *) echo "usage: $0 [--release|--dev]" >&2; exit 2 ;;
esac

cargo +nightly build --lib --target wasm32-unknown-unknown $FLAGS
# Respect a global `build.target-dir` (~/.cargo/config.toml) instead of assuming ./target.
TARGET_DIR="$(cargo metadata --format-version 1 --no-deps | python3 -c 'import json,sys; print(json.load(sys.stdin)["target_directory"])')"
WASM="$TARGET_DIR/wasm32-unknown-unknown/$PROFILE/mhw_log_viewer.wasm"
[[ -f "$WASM" ]] || { echo "missing $WASM" >&2; exit 1; }

wasm-bindgen "$WASM" --out-dir www/src/wasm --target web --no-typescript
rm -rf www/public/sample-logs && cp -r sample-logs www/public/sample-logs
echo "wasm ready: www/src/wasm  (cd www && pnpm install && pnpm dev)"
