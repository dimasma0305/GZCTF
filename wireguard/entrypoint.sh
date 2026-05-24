#!/bin/bash
# WireGuard sidecar entrypoint.
#
# Responsibilities:
#   1. On first boot: generate the server keypair into /config so GZCTF can
#      read the pubkey and embed it in client .conf files.
#   2. Wait for GZCTF to write the initial /config/wg0.conf with the server
#      [Interface] block + zero or more [Peer] blocks.
#   3. Bring up the wg0 interface.
#   4. Watch wg0.conf for changes (GZCTF's AdWireGuardSyncService rewrites it
#      whenever the AdVpnPeer table changes) and run `wg syncconf` so the
#      change applies in-place.
#
# Everything else (peer cleanup, key revocation) is driven by the GZCTF side
# rewriting wg0.conf; the sidecar is intentionally dumb.

set -euo pipefail

CONFIG_DIR="${CONFIG_DIR:-/config}"
WG_CONF="${CONFIG_DIR}/wg0.conf"
SERVER_PRIV="${CONFIG_DIR}/server.key"
SERVER_PUB="${CONFIG_DIR}/server.pub"
INTERFACE="${WG_INTERFACE:-wg0}"

mkdir -p "$CONFIG_DIR"

if [[ ! -f "$SERVER_PRIV" ]]; then
  echo "[wg-sidecar] no server keypair found, generating new keys"
  wg genkey | tee "$SERVER_PRIV" | wg pubkey > "$SERVER_PUB"
  chmod 600 "$SERVER_PRIV"
fi

echo "[wg-sidecar] server pubkey: $(cat "$SERVER_PUB")"

# Pre-flight: enable IPv4 forwarding inside the netns so traffic from VPN
# clients can route into the A&D challenge network.
if [[ -w /proc/sys/net/ipv4/ip_forward ]]; then
  echo 1 > /proc/sys/net/ipv4/ip_forward || true
fi

# Wait for GZCTF to seed the config the first time. GZCTF's
# AdWireGuardSyncService writes wg0.conf right after detecting the server
# keypair, so this normally takes <1s after first boot. Cap the wait so we
# don't block forever if the sync service is broken.
for i in {1..120}; do
  if [[ -f "$WG_CONF" ]]; then
    break
  fi
  if (( i == 1 )); then
    echo "[wg-sidecar] waiting for $WG_CONF (written by GZCTF's AdWireGuardSyncService)..."
  fi
  sleep 1
done

if [[ ! -f "$WG_CONF" ]]; then
  echo "[wg-sidecar] FATAL: $WG_CONF never appeared after 120s. Is GZCTF running?"
  exit 1
fi

echo "[wg-sidecar] bringing up $INTERFACE"
wg-quick up "$WG_CONF" || {
  echo "[wg-sidecar] wg-quick up failed; dumping config:"
  cat "$WG_CONF" || true
  exit 1
}

# Graceful teardown on SIGTERM/SIGINT.
cleanup() {
  echo "[wg-sidecar] received signal, bringing $INTERFACE down"
  wg-quick down "$WG_CONF" || true
  exit 0
}
trap cleanup SIGTERM SIGINT

echo "[wg-sidecar] watching $WG_CONF for changes"
while inotifywait -qq -e close_write,moved_to "$CONFIG_DIR"; do
  if [[ -f "$WG_CONF" ]]; then
    echo "[wg-sidecar] $(date -Iseconds) — config changed, running wg syncconf"
    if ! wg syncconf "$INTERFACE" <(wg-quick strip "$WG_CONF"); then
      echo "[wg-sidecar] wg syncconf failed — leaving previous config in place"
    fi
  fi
done
