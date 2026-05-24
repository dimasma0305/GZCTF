#!/bin/sh
#
# Reference A&D checker for the gzctf/echo-http:test challenge.
#
# The challenge serves the current /flag as plain text at GET /. The
# AdCheckerExecutor wraps this image with the live team-service target
# (IP + port) and the round's planted flag, so all we do is:
#
#   1. GET http://<target>/ — fail closed on transport errors.
#   2. Confirm the response body contains the round's flag.
#
# Exit code → AdCheckStatus mapping (enochecker3):
#   0 = Ok            (flag retrieved as planted)
#   1 = Mumble        (service responded but flag wrong / missing)
#   2 = Offline       (TCP refused / timeout / curl couldn't connect)
#   3 = InternalError (checker bug / missing env)
set -u

if [ -z "${GZCTF_TARGET_IP:-}" ] || [ -z "${GZCTF_TARGET_PORT:-}" ]; then
    echo "missing GZCTF_TARGET_IP or GZCTF_TARGET_PORT" >&2
    exit 3
fi

if [ -z "${GZCTF_FLAG:-}" ]; then
    # No flag context means the platform is in warmup OR didn't plant
    # yet for our team. We can still probe reachability but cannot
    # verify content — treat as Mumble (service might be up, content
    # not yet syncable).
    echo "no GZCTF_FLAG; falling back to reachability-only" >&2
    if curl -sS --max-time 5 -o /dev/null "http://${GZCTF_TARGET_IP}:${GZCTF_TARGET_PORT}/"; then
        exit 1
    fi
    exit 2
fi

body="$(curl -sS --max-time 5 "http://${GZCTF_TARGET_IP}:${GZCTF_TARGET_PORT}/" 2>&1)"
curl_exit=$?

# curl exit codes we treat as Offline (network-layer failure):
#   6 = couldn't resolve host
#   7 = couldn't connect
#  28 = operation timeout
#  56 = receive failure
# anything else non-zero with no body → Offline too.
case "$curl_exit" in
    0) ;;
    6|7|28|56)
        echo "curl exit $curl_exit (offline): $body" >&2
        exit 2
        ;;
    *)
        echo "curl exit $curl_exit: $body" >&2
        exit 2
        ;;
esac

case "$body" in
    *"$GZCTF_FLAG"*)
        # Quiet on success — Ok rows don't store ErrorMessage.
        exit 0
        ;;
    *)
        # Use head -c so a maliciously huge response doesn't fill the
        # log buffer the executor truncates at 4096 chars anyway.
        snippet="$(printf '%s' "$body" | head -c 200)"
        echo "flag not present in body. got: ${snippet}" >&2
        exit 1
        ;;
esac
