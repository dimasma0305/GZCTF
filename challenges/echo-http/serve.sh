#!/bin/sh
# socat invokes this per connection with stdin/stdout = the TCP socket.
# Read (and discard) the request headers up to the blank line so curl's
# `\r\n` request terminator is consumed, then write a basic HTTP reply
# with the current contents of the flag file (read fresh — the platform
# overwrites it each tick).
#
# The platform sets GZCTF_FLAG_FILE to the live flag path: /flag on Docker
# (read-only bind mount), /gzctf-flag/flag on Kubernetes (read-only volume
# the flag-writer sidecar pulls into). Fall back to /flag if unset.
while IFS= read -r line; do
    line="${line%$'\r'}"
    [ -z "$line" ] && break
done

flag="$(cat "${GZCTF_FLAG_FILE:-/flag}" 2>/dev/null || echo 'no flag yet')"
body="flag is: ${flag}
"

printf 'HTTP/1.1 200 OK\r\n'
printf 'Content-Type: text/plain\r\n'
printf 'Content-Length: %d\r\n' "${#body}"
printf 'Connection: close\r\n'
printf '\r\n'
printf '%s' "$body"
