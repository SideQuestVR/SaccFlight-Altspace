#!/usr/bin/env bash
# Run the scripted drive test on ground vehicles, one after another, and print each summary.
#   Tools/drive_test.sh "Baron" "Tank" ...        (no arguments: every ground vehicle)
# Take the editor lock first (Tools/editor_lock.sh): this enters Play mode.
cd "$(dirname "$0")/.."
DIR=Library/FlightWorld

send() {  # send "<command>" [timeout seconds]
  rm -f "$DIR/result.txt"
  printf '%s' "$1" > "$DIR/command.txt"
  local tries=$(( ${2:-120} / 2 ))
  for _ in $(seq 1 "$tries"); do
    if [ -f "$DIR/result.txt" ]; then cat "$DIR/result.txt"; return 0; fi
    sleep 2
  done
  echo "no result for: $1"; return 1
}

if [ "$#" -eq 0 ]; then set -- "Baron" "Iccarus" "Shoreline" "Thunder" "Tank"; fi
for vehicle in "$@"; do
  log="Logs/drive_test_$(echo "$vehicle" | tr ' ' '_').txt"
  rm -f "$log"
  printf '%s' "$vehicle" > "$DIR/test_target.txt"
  send "menu:FlightWorld/Flight/Test/Arm Scripted Drive" 60 >/dev/null
  send play 60 >/dev/null
  for _ in $(seq 1 50); do grep -q DONE "$log" 2>/dev/null && break; sleep 3; done
  echo "== $vehicle: $(grep -a -E 'SUMMARY' "$log" | cut -c1-600)"
  grep -q SUMMARY "$log" 2>/dev/null || { echo "   no summary; last lines:"; tail -4 "$log" | cut -c1-200; send stop 60 >/dev/null; }
  sleep 6
done
echo "ALL DONE"
