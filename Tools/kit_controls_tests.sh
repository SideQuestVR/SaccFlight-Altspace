#!/usr/bin/env bash
# Every vehicle kit, in an empty world, worked with real input (Assets/Editor/KitControlsTest.cs).
#   Tools/kit_controls_tests.sh [slug ...]       touch Library/FlightWorld/kit_live.txt first for the published copies
# Needs the editor open (take Tools/editor_lock.sh first). Results: Logs/kit_controls_<slug>.txt.
set -u
cd "$(dirname "$0")/.."
KITS=Library/FlightWorld/kits
if [ $# -gt 0 ]; then slugs="$*"; else slugs=$(ls "$KITS/windows" | grep -v -E 'manifest|^windows$'); fi
python -m http.server 42071 --bind 127.0.0.1 --directory "$KITS" >/dev/null 2>&1 &
server=$!
trap 'kill $server 2>/dev/null' EXIT
sleep 1
for slug in $slugs; do
  log="Logs/kit_controls_$slug.txt"
  rm -f "$log"
  printf '%s' "$slug" > Library/FlightWorld/kit_only.txt
  Tools/remote.sh menu "FlightWorld/Snippets/Test/Arm Controls Test" 120 >/dev/null
  grep -q '^armed' "$log" 2>/dev/null || { echo "== $slug: not armed"; continue; }
  Tools/remote.sh play >/dev/null
  for _ in $(seq 1 80); do grep -q DONE "$log" 2>/dev/null && break; sleep 3; done
  echo "== $slug"
  grep -E '^(PASS|FAIL)|^   \[(Error|Exception|Assert)' "$log" | cut -c1-400
  if ! grep -q DONE "$log"; then echo "   no DONE: stopping"; Tools/remote.sh stop >/dev/null; Tools/remote.sh menu "FlightWorld/Snippets/Test/Restore After Controls Test" 60 >/dev/null; fi
  sleep 12
done
: > Library/FlightWorld/kit_only.txt
