#!/bin/sh
#
# GZCTF jump-host bootstrap. Generates host keys if missing, kicks off
# the periodic /etc/passwd refresher (so sshd accepts new challenge ids
# without a restart), and execs sshd in the foreground so tini can reap
# it cleanly.
set -eu

if [ -z "${GZCTF_URL:-}" ]; then
    echo "[entrypoint] FATAL: GZCTF_URL not set" >&2
    exit 1
fi

if [ -z "${INTERNAL_SECRET:-}" ]; then
    echo "[entrypoint] FATAL: INTERNAL_SECRET not set" >&2
    exit 1
fi

# Hand the internal secret + gzctf URL to AuthorizedKeysCommand via a
# 0640 root:nobody env file. sshd strips most environment from the
# AuthorizedKeysCommand process (runs as `AuthorizedKeysCommandUser
# nobody`, no shell env inheritance), so the script has to source this
# explicitly. Same for ForceCommand's exec.sh, which runs as the
# authenticated user.
cat > /etc/gzctf-ssh.env <<EOF
GZCTF_URL=${GZCTF_URL}
INTERNAL_SECRET=${INTERNAL_SECRET}
EOF
# World-readable inside the container: AuthorizedKeysCommand runs as
# `nobody` and ForceCommand runs as `ctf`, so the file has to be
# readable by both. The container has only those three accounts and
# they all run our scripts; there's no untrusted local process to
# protect against. The secret never leaves the docker network.
chmod 0644 /etc/gzctf-ssh.env

# Host keys: generated once and persisted under /etc/ssh. If the volume
# isn't writable we lose host-key continuity across restarts (clients
# get the "remote host identification changed" warning); operator can
# bind-mount /etc/ssh to a volume to avoid that.
for type in ed25519 ecdsa; do
    f="/etc/ssh/ssh_host_${type}_key"
    if [ ! -f "$f" ]; then
        echo "[entrypoint] generating $type host key"
        ssh-keygen -q -t "$type" -f "$f" -N ""
    fi
done

# Initial passwd sync — block on it so the first connection after start
# has the right alias set. After that the loop runs every 60s.
echo "[entrypoint] initial /etc/passwd sync..."
if ! /usr/local/bin/refresh-users.sh; then
    echo "[entrypoint] initial sync failed (gzctf not ready?); the loop will retry" >&2
fi

# Background refresh loop. Restart on crash so a temporary gzctf outage
# doesn't permanently freeze our user table.
(
    while true; do
        sleep 60
        if ! /usr/local/bin/refresh-users.sh; then
            echo "[refresh-loop] refresh failed; will retry next tick" >&2
        fi
    done
) &

# -D = no daemonize, -e = log to stderr (so docker logs sees it).
echo "[entrypoint] starting sshd"
exec /usr/sbin/sshd -D -e
