#!/bin/sh
#
# Pull the active A&D challenge IDs from gzctf and ensure /etc/passwd
# has one alias per id, so sshd accepts `ssh <id>@host` for each. All
# aliases share UID 1001 / GID 1001 — there's no per-challenge isolation
# at the host level; isolation lives downstream in the container the
# ForceCommand attaches to.
#
# Idempotent: removes ad-managed entries that are no longer in the
# active set, leaves untouched ones (root, ctf, nobody, etc).
set -eu

PASSWD=/etc/passwd
TMP="$(mktemp)"
trap 'rm -f "$TMP"' EXIT

# Marker comment in each managed line so we can identify+rewrite without
# touching unrelated entries (sshd auth users like sshd itself).
MARKER="# gzctf-ad-managed"

resp="$(curl -sS \
    -H "X-Gzctf-Internal-Auth: ${INTERNAL_SECRET}" \
    "${GZCTF_URL%/}/api/Internal/Ad/Ssh/Challenges")"

if ! echo "$resp" | jq -e '.challengeIds' >/dev/null 2>&1; then
    echo "[refresh-users] unexpected response from gzctf:" >&2
    echo "$resp" | head -c 200 >&2
    exit 1
fi

ids="$(echo "$resp" | jq -r '.challengeIds[]?')"

# Filter out any prior gzctf-ad-managed lines, keep everything else.
grep -v "$MARKER" "$PASSWD" > "$TMP"

# Append one entry per active challenge id.
if [ -n "$ids" ]; then
    echo "$ids" | while IFS= read -r id; do
        # Defensive: ids are integers from the DB; refuse anything that
        # could escape into a passwd field. (jq -r should already give
        # us plain digits, but the shell pipe-line is best to be paranoid.)
        case "$id" in
            ''|*[!0-9]*) continue ;;
        esac
        echo "${id}:x:1001:1001:gzctf challenge ${id} ${MARKER}:/tmp:/bin/sh" >> "$TMP"
    done
fi

# Atomic swap. Preserve perms (passwd is 0644 root:root).
cat "$TMP" > "$PASSWD"

count="$(grep -c "$MARKER" "$PASSWD" || true)"
echo "[refresh-users] /etc/passwd synced — ${count} challenge alias(es) live"
