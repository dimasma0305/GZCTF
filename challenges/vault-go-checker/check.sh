#!/bin/sh
#
# Checker for gzctf/vault-go:test.
#
#   SLA      : /health up; /secret without the token is rejected (403).
#   GET flag : read /secret with the weak token and match the round flag.
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

# Functionality: the auth gate must reject a missing/blank token.
code="$(curl -sS --max-time 5 -o /dev/null -w '%{http_code}' "$B/secret" 2>&1)"
if [ "$code" != "403" ]; then
    echo "auth gate broken: /secret (no token) -> $code" >&2
    exit 1
fi

if [ -z "${GZCTF_FLAG:-}" ]; then
    echo "no GZCTF_FLAG (warmup)" >&2
    exit 1
fi

flag="$(curl -sS --max-time 5 "$B/secret?token=admin" 2>&1)"
case "$flag" in
    *"$GZCTF_FLAG"*) exit 0 ;;
    *)
        echo "secret missing flag: $(printf '%s' "$flag" | head -c 100)" >&2
        exit 1
        ;;
esac
