#!/bin/sh
#
# sshd ForceCommand wrapper. Pinned in authorized_keys by
# lookup-key.sh with the resolved identifiers, so by the time we run
# the caller is already authenticated AND tied to a specific (user,
# challenge, container).
#
# Args (positional, set by lookup-key.sh in the authorized_keys command):
#   $1 = containerGuid (the DB Guid, used to look up the docker container id)
#   $2 = userId        (echoed in logs only; auth already enforced)
#   $3 = challengeId   (echoed in logs only)
#
# Opens a WebSocket to gzctf's internal exec endpoint and pumps the
# user's terminal bytes both ways via websocat. websocat handles binary
# frames and proxies stdin↔stdout without buffering.
set -eu

# sshd ForceCommand inherits very little env from the parent sshd
# process — re-source the same env file lookup-key.sh uses so we have
# GZCTF_URL + INTERNAL_SECRET for the WS handshake.
# shellcheck disable=SC1091
. /etc/gzctf-ssh.env

container_guid="${1:-}"
user_id="${2:-}"
challenge_id="${3:-}"

if [ -z "$container_guid" ]; then
    printf 'exec.sh: missing container_guid\r\n' >&2
    exit 1
fi

# The container-side docker exec always opens `sh` (the WS endpoint
# normalizes the shell parameter to /sh|bash/), so for one-shot
# commands (`ssh 76@host 'cat /flag'`) we have to inject the command
# into the shell's stdin instead of using -c.
oneshot="${SSH_ORIGINAL_COMMAND:-}"

# If we got a controlling TTY (the user did `ssh user@host` without
# `-T`), put it into raw mode so the container's TTY does all the
# echo/line-discipline work. Without this, every keystroke gets
# double-echoed.
if [ -t 0 ]; then
    stty raw -echo -ixon -isig 2>/dev/null || true
    trap 'stty sane 2>/dev/null || true' EXIT INT TERM
fi

encoded_auth="$(printf %s "$INTERNAL_SECRET" | jq -sRr @uri)"

# Translate http(s) → ws(s) for websocat.
ws_url="$(printf %s "${GZCTF_URL%/}" | sed -e 's|^http://|ws://|' -e 's|^https://|wss://|')"
ws_url="${ws_url}/api/Internal/Ad/Ssh/Exec?auth=${encoded_auth}&container=${container_guid}&shell=sh"

# websocat flags:
#   --binary           don't text-decode incoming frames
#   --no-close         don't proactively send WS Close on local stdin
#                      EOF — wait for the container side to close. For
#                      non-interactive ssh (`ssh user@host cmd`) the
#                      client closes stdin immediately, and -E /
#                      --exit-on-eof would tear down the WS before
#                      docker exec gets its output back. So we exit
#                      when the WS itself closes (server side closes
#                      after bash exits).
#   --ping-interval=20 keep middleboxes from idle-timing the WS.
# websocat's stderr prints "WebSocketError: I/O failure" when the
# remote closes abruptly — harmless but visible noise in the user's
# terminal at session end. Suppress it.
if [ -n "$oneshot" ]; then
    # Inject command + exit so the container's bash terminates after
    # running it. After bash exits the WS closes from the gzctf side,
    # websocat receives the close and exits, flushing the buffered
    # output to our (ssh client's) stdout in the process.
    printf '%s\nexit\n' "$oneshot" | \
        exec websocat --binary --no-close --ping-interval=20 "$ws_url" 2>/dev/null
else
    exec websocat --binary --no-close --ping-interval=20 "$ws_url" 2>/dev/null
fi
