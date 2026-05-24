FROM alpine:3.21

# openssh-server: sshd
# ca-certificates + curl: AuthorizedKeysCommand calls back to gzctf over HTTPS
# jq: parse the lookup JSON in shell
# coreutils: shasum + base64 with predictable flags (busybox versions differ)
# websocat: stdin/stdout WebSocket relay used as ForceCommand target
# tini: PID 1 for clean signal handling
RUN apk add --no-cache \
        openssh-server \
        ca-certificates \
        curl \
        jq \
        coreutils \
        tini \
        websocat \
    && rm -rf /var/cache/apk/*

# Shared OS user every challenge sub-account aliases to via /etc/passwd.
# Home is /tmp because the ForceCommand wrapper exec's away — nothing
# ever writes to it.
RUN addgroup -g 1001 ctf && \
    adduser -D -H -u 1001 -G ctf -s /bin/sh -h /tmp ctf

COPY sshd_config /etc/ssh/sshd_config
COPY entrypoint.sh /usr/local/bin/entrypoint.sh
COPY refresh-users.sh /usr/local/bin/refresh-users.sh
COPY lookup-key.sh /usr/local/bin/lookup-key.sh
COPY exec.sh /usr/local/bin/exec.sh

RUN chmod 755 /usr/local/bin/entrypoint.sh \
              /usr/local/bin/refresh-users.sh \
              /usr/local/bin/lookup-key.sh \
              /usr/local/bin/exec.sh && \
    chown root:root /usr/local/bin/lookup-key.sh && \
    chmod 0700 /etc/ssh && \
    mkdir -p /run/sshd && \
    chmod 0755 /run/sshd

EXPOSE 22

ENTRYPOINT ["/sbin/tini", "--", "/usr/local/bin/entrypoint.sh"]
