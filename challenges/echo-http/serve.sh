#!/bin/sh
# socat invokes this per connection with stdin/stdout = the TCP socket.
# Read (and discard) the request headers up to the blank line so curl's
# `\r\n` request terminator is consumed, then write a basic HTTP reply
# with the current contents of /flag (read fresh — the platform
# overwrites this file each tick).
while IFS= read -r line; do
    line="${line%$'\r'}"
    [ -z "$line" ] && break
done

flag="$(cat /flag 2>/dev/null || echo 'no flag yet')"
body="flag is: ${flag}
"

printf 'HTTP/1.1 200 OK\r\n'
printf 'Content-Type: text/plain\r\n'
printf 'Content-Length: %d\r\n' "${#body}"
printf 'Connection: close\r\n'
printf '\r\n'
printf '%s' "$body"
