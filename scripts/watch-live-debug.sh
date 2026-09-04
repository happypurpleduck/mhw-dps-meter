#!/usr/bin/env bash
# Watch live DPS meter diagnostics during a hunt.
set -euo pipefail

LOG_DIR="${1:-/mnt/steam/SteamLibrary/steamapps/common/Monster Hunter World/nativePC/plugins/CSharp/MhwDpsMeter/logs}"
JSON="$LOG_DIR/live-debug.json"
LOG="$LOG_DIR/live-debug.log"

echo "Watching:"
echo "  $JSON"
echo "  $LOG"
echo "Press F6 in-game (or use the F9 panel button) to force a dump. Ctrl-C to stop."
echo

mkdir -p "$LOG_DIR"
touch "$LOG"

if command -v jq >/dev/null 2>&1; then
  (
    while true; do
      if [[ -f "$JSON" ]]; then
        clear
        date
        jq '{
          at, inQuest, questId, questName, elapsed, status, damageSource, lastError, layout,
          partySize, localName, hasPackedTable, damageBase, partyArray,
          hookTotal, hookSlots, fallbackLocalDamage, overlayNames, overlayDamage,
          slots: [.slots[] | {slot, shown, shownName, rawDamage, partyName, sessionName, ptrOk, ptr, partyNameHex, why}]
        }' "$JSON" 2>/dev/null || cat "$JSON"
      else
        echo "waiting for $JSON (depart on a quest or press F6)"
      fi
      sleep 1
    done
  ) &
  jq_pid=$!
  trap 'kill $jq_pid 2>/dev/null || true' EXIT
fi

tail -n 20 -F "$LOG"
