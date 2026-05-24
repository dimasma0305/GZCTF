# Minimal WireGuard sidecar for GZCTF A&D. Boots wg0 from a shared /config
# volume that GZCTF writes into. Watches wg0.conf with inotify and runs
# `wg syncconf` on change so peer additions/removals take effect without
# restarting the container.
FROM alpine:3.20

RUN apk add --no-cache \
        wireguard-tools \
        bash \
        iproute2 \
        iptables \
        ip6tables \
        inotify-tools \
        openresolv

COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# UDP listen port for the WG handshake.
EXPOSE 51820/udp

ENTRYPOINT ["/entrypoint.sh"]
