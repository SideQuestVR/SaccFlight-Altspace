#!/usr/bin/env bash
# Run the scripted flight test on several vehicles, one after another, and print each summary.
#   Tools/flight_all.sh "SF-2" "Stunt Plane" ...        (no arguments: every vehicle in the fleet)
cd "$(dirname "$0")/.."
if [ "$#" -eq 0 ]; then
  set -- "SF-1 1" "SF-2" "SB-1" "Stunt Plane" "Wasp WX3" "SX-TR3" "SC-1" "SH-1" "SH-2" "SH-3" "SD-1" "GRVR-1" "Balloon"
fi
for vehicle in "$@"; do
  log="Logs/flight_test_$(echo "$vehicle" | tr ' ' '_').txt"
  rm -f "$log"
  result=$(bash Tools/remote.sh flight "$vehicle" 2>&1 | tail -1 | cut -c1-230)
  echo "== $vehicle: $result"
  grep -a -m1 "STUCK" "$log" | cut -c1-200
  # wait for the editor to be back in edit mode before arming the next one
  for _ in $(seq 1 20); do grep -q DONE "$log" 2>/dev/null && break; sleep 2; done
  sleep 6
done
echo "ALL DONE"
