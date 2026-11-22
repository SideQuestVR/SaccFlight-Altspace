#!/usr/bin/env bash
# Drive the open Unity editor through Assets/Editor/FlightWorldRemote.cs (no MCP server needed).
#
#   Tools/remote.sh compile                 refresh assets, wait for scripts to recompile, print compile errors
#   Tools/remote.sh menu "Some/Menu Item"   run a menu item and wait for it to return   [timeout seconds]
#   Tools/remote.sh play | stop
#   Tools/remote.sh shot name "x,y,z" "tx,ty,tz" [fov]    -> Library/FlightWorld/shots/name.jpg
#   Tools/remote.sh flight "SF-1 1"         arm the scripted flight test for a vehicle, play, print the summary
#   Tools/remote.sh crash "SF-1 1"          fly a vehicle into the ground, check the explosion and the respawn
set -u
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

case "${1:-}" in
  compile)
    before=$(cat "$DIR/ready.txt" 2>/dev/null)
    send refresh 60 >/dev/null
    for _ in $(seq 1 80); do
      [ "$(cat "$DIR/ready.txt" 2>/dev/null)" != "$before" ] && { echo "recompiled"; break; }
      sleep 3
    done
    errors=$(grep -a -E '^\[compile\]' "$DIR/log.txt" 2>/dev/null | sort -u | cut -c1-320)
    if [ -n "$errors" ]; then echo "$errors"; exit 1; fi
    echo "no compile errors"
    ;;
  menu) send "menu:$2" "${3:-180}" ;;
  play|stop) send "$1" 60 ;;
  shot) send "shot:$2;$3;$4;${5:-60}" 60 ;;
  flight)
    log="Logs/flight_test_$(echo "$2" | tr ' ' '_').txt"
    printf '%s' "$2" > "$DIR/test_target.txt"
    send "menu:FlightWorld/Flight/Test/Arm Scripted Flight" 60 >/dev/null
    send play 60 >/dev/null
    for _ in $(seq 1 60); do grep -q DONE "$log" 2>/dev/null && break; sleep 3; done
    grep -E 'SUMMARY' "$log" || { echo "no summary; last lines:"; tail -5 "$log"; }
    sleep 4
    ;;
  crash)
    log="Logs/crash_test_$(echo "$2" | tr ' ' '_').txt"
    printf '%s' "$2" > "$DIR/test_target.txt"
    send "menu:FlightWorld/Flight/Test/Arm Crash Test" 60 >/dev/null
    send play 60 >/dev/null
    for _ in $(seq 1 60); do grep -q DONE "$log" 2>/dev/null && break; sleep 3; done
    grep -v -E '^[0-9.]+,' "$log" | tail -8
    sleep 4
    ;;
  *) sed -n '2,10p' "$0" ;;
esac
