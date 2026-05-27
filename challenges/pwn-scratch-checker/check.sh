#!/bin/sh
#
# Checker for gzctf/pwn-scratch:test.
#
#   SLA      : /health up.
#   GET flag : GET / and match the round flag.
#
# Exit: 0=Ok, 1=Mumble, 2=Offline, 3=InternalError.
set -u

if [ -z "${GZCTF_TARGET_IP:-}" ] || [ -z "${GZCTF_TARGET_PORT:-}" ]; then
    echo "missing GZCTF_TARGET_IP / GZCTF_TARGET_PORT" >&2
    exit 3
fi
B="http://${GZCTF_TARGET_IP}:${GZCTF_TARGET_PORT}"

if ! curl -sS --max-time 5 -o /dev/null "$B/health"; then
    echo "health unreachable" >&2
    exit 2
fi

if [ -z "${GZCTF_FLAG:-}" ]; then
    echo "no GZCTF_FLAG (warmup)" >&2
    exit 1
fi

body="$(curl -sS --max-time 5 "$B/" 2>&1)"
case "$body" in
    *"$GZCTF_FLAG"*) exit 0 ;;
    *)
        echo "flag not in body: $(printf '%s' "$body" | head -c 100)" >&2
        exit 1
        ;;
esac
