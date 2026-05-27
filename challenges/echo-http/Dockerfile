FROM alpine:3.21

# Keep out of the host's daily `docker image prune -af` (running team
# containers usually hold this, but label it too for safety). The prune cron
# excludes label org.gzctf.keep=true.
LABEL org.gzctf.keep="true"

RUN apk add --no-cache socat

# No baked flag: the platform supplies the live flag at $GZCTF_FLAG_FILE
# (Docker: /flag bind-mount; K8s: /gzctf-flag/flag via the flag-pull sidecar).
# serve.sh reads $GZCTF_FLAG_FILE fresh per request; a missing file = warmup.
COPY serve.sh /serve.sh
RUN chmod +x /serve.sh

EXPOSE 80

# socat keeps re-listening on 80, forks per connection, runs serve.sh.
# Reads /flag at request time so per-tick rotation is visible
# immediately — no caching, no reload-needed.
CMD ["socat", "-T", "5", "TCP-LISTEN:80,reuseaddr,fork", "SYSTEM:/serve.sh"]
