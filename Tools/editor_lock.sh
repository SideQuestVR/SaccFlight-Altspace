#!/usr/bin/env bash
# One Unity editor, several workers: take this lock before anything that makes the editor compile, build
# the scene, enter Play mode or publish, and give it back when the scripts compile cleanly again.
#
#   Tools/editor_lock.sh acquire <who> [wait seconds, default 1800]   blocks until the lock is free
#   Tools/editor_lock.sh release <who>
#   Tools/editor_lock.sh status
#
# Rule: while you hold the lock you may leave scripts in a broken state; when you release it,
# `Tools/remote.sh compile` must print "no compile errors".
cd "$(dirname "$0")/.."
LOCK=Library/FlightWorld/editor.lock
case "${1:-}" in
  acquire)
    who="${2:?who}"; wait="${3:-1800}"
    for _ in $(seq 1 $(( wait / 3 ))); do
      if mkdir "$LOCK" 2>/dev/null; then
        printf '%s %s\n' "$who" "$(date '+%H:%M:%S')" > "$LOCK/owner"
        echo "lock acquired by $who"; exit 0
      fi
      [ "$(cut -d' ' -f1 "$LOCK/owner" 2>/dev/null)" = "$who" ] && { echo "lock already held by $who"; exit 0; }
      sleep 3
    done
    echo "lock still held by: $(cat "$LOCK/owner" 2>/dev/null)"; exit 1
    ;;
  release)
    who="${2:?who}"
    if [ "$(cut -d' ' -f1 "$LOCK/owner" 2>/dev/null)" = "$who" ]; then rm -rf "$LOCK"; echo "lock released by $who"
    else echo "not released: lock is held by: $(cat "$LOCK/owner" 2>/dev/null || echo nobody)"; exit 1; fi
    ;;
  status) [ -d "$LOCK" ] && echo "held by: $(cat "$LOCK/owner")" || echo "free" ;;
  *) sed -n '2,11p' "$0" ;;
esac
