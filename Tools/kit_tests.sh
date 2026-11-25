#!/usr/bin/env bash
# Load each vehicle kit through its snippet in Play mode and try it (Assets/Editor/KitPlayTest.cs).
#
#   Tools/kit_tests.sh                    every kit that has a bundle
#   Tools/kit_tests.sh sacc-sf-1 sacc-tank
#
# Needs the open Unity editor (take Tools/editor_lock.sh first) and the kits built
# (FlightWorld/Snippets/Build Kit Prefabs, then Build Kit Bundles). Serves the bundles from
# Library/FlightWorld/kits on port 42071 while it runs. Results: Logs/kit_test_<slug>.txt.
set -u
cd "$(dirname "$0")/.."
KITS=Library/FlightWorld/kits
if [ $# -gt 0 ]; then slugs="$*"; else slugs=$(ls "$KITS/windows" | grep -v -E 'manifest|^windows$'); fi

python -m http.server 42071 --bind 127.0.0.1 --directory "$KITS" >/dev/null 2>&1 &
server=$!
trap 'kill $server 2>/dev/null' EXIT
sleep 1

for slug in $slugs; do
  log="Logs/kit_test_$slug.txt"
  rm -f "$log"
  printf '%s' "$slug" > Library/FlightWorld/kit_only.txt
  Tools/remote.sh menu "FlightWorld/Snippets/Test/Arm Kit Test" 60 >/dev/null
  if ! grep -q '^armed' "$log" 2>/dev/null; then echo "== $slug: not armed"; cat "$log" 2>/dev/null; continue; fi
  Tools/remote.sh play >/dev/null
  for _ in $(seq 1 80); do grep -q DONE "$log" 2>/dev/null && break; sleep 3; done
  echo "== $slug"
  grep -E '^(PASS|FAIL)|^   \[(Error|Exception|Assert)' "$log" | cut -c1-260
  grep -q DONE "$log" || { echo "   no DONE: stopping Play"; Tools/remote.sh stop >/dev/null; Tools/remote.sh menu "FlightWorld/Snippets/Test/Restore Page" 30 >/dev/null; }
  # back in edit mode before the next one
  for _ in $(seq 1 20); do [ "$(Tools/remote.sh menu 'FlightWorld/Snippets/Test/Is Editing' 10 2>/dev/null | grep -c 'menu ok')" = 1 ] && grep -q editing Library/FlightWorld/kit_state.txt 2>/dev/null && break; sleep 2; done
done
: > Library/FlightWorld/kit_only.txt
