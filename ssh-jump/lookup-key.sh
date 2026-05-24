#!/bin/sh
#
# sshd AuthorizedKeysCommand. Called once per pubkey offered during
# auth. Arguments come from sshd via the %u %k %t %f tokens in
# sshd_config — see the AuthorizedKeysCommand line there.
#
#   $1 = requested username  (challenge id, e.g. "76")
#   $2 = base64 of the offered pubkey blob (no algorithm prefix)
#   $3 = key type            (e.g. "ssh-ed25519")
#   $4 = pre-computed SHA256 fingerprint, OpenSSH format
#        ("SHA256:<base64>") — saves us a base64-decode + sha256 hop
#
# We forward (fingerprint, challenge id) to gzctf's internal lookup
# endpoint. If it answers 200, we emit a single authorized_keys line
# with a ForceCommand that pins the resolved (containerGuid, userId,
# challengeId) — sshd will then re-auth the offered key against that
# line and run the ForceCommand on success.
#
# On any failure we emit nothing → sshd treats it as "no matching key"
# and refuses the connection.
set -eu

# sshd runs us as AuthorizedKeysCommandUser (nobody) with a stripped
# env, so the GZCTF_URL / INTERNAL_SECRET that the operator set on the
# container env have to be re-sourced from disk. The entrypoint writes
# them to this file at boot.
# shellcheck disable=SC1091
. /etc/gzctf-ssh.env

username="$1"
keydata="$2"
keytype="$3"
fp="$4"

# Sanity: the requested username must be a positive integer (challenge
# id). Refuse anything else without round-tripping to gzctf.
case "$username" in
    ''|*[!0-9]*) exit 0 ;;
esac

# Some sshd builds don't pass %f — recompute if missing.
if [ -z "${fp:-}" ]; then
    fp="SHA256:$(printf %s "$keydata" | base64 -d 2>/dev/null \
        | sha256sum | awk '{print $1}' \
        | xxd -r -p | base64 | tr -d '=' | tr -d '\n' 2>/dev/null || true)"
    [ -z "$fp" ] && exit 0
fi

# Curl with --fail-with-body so HTTP 4xx / 5xx exits non-zero; --silent
# keeps sshd's auth.log clean (the script's stderr is captured there).
resp="$(curl -sS --fail-with-body --max-time 5 \
    -H "X-Gzctf-Internal-Auth: ${INTERNAL_SECRET}" \
    --data-urlencode "fingerprint=${fp}" \
    --data-urlencode "challenge=${username}" \
    --get \
    "${GZCTF_URL%/}/api/Internal/Ad/Ssh/Lookup" 2>/dev/null)" || exit 0

container_guid="$(echo "$resp" | jq -r '.containerGuid // empty')"
user_id="$(echo "$resp" | jq -r '.userId // empty')"
challenge_id="$(echo "$resp" | jq -r '.challengeId // empty')"

[ -z "$container_guid" ] && exit 0
[ -z "$user_id" ] && exit 0
[ -z "$challenge_id" ] && exit 0

# Emit the authorized_keys line. `restrict` would also disable PTY,
# which we need for interactive shells — list the individual flags
# instead. The ForceCommand args are space-separated; exec.sh splits
# them positionally.
printf 'command="/usr/local/bin/exec.sh %s %s %s",no-agent-forwarding,no-port-forwarding,no-X11-forwarding,no-user-rc %s %s\n' \
    "$container_guid" "$user_id" "$challenge_id" "$keytype" "$keydata"
