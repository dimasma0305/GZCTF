FROM alpine:3.21

# Keep out of the host's daily `docker image prune -af` (running team
# containers usually hold this, but label it too for safety). The prune cron
# excludes label org.gzctf.keep=true.
LABEL org.gzctf.keep="true"

RUN apk add --no-cache socat

# Default "warmup" flag — the platform rotates /flag each tick via docker
# exec. serve.sh reads /flag fresh per request (no GZCTF_FLAG env var).
RUN echo 'flag{warmup-no-round-yet}' > /flag && chmod 644 /flag

COPY serve.sh /serve.sh
RUN chmod +x /serve.sh

EXPOSE 80

# socat keeps re-listening on 80, forks per connection, runs serve.sh.
# Reads /flag at request time so per-tick rotation is visible
# immediately — no caching, no reload-needed.
CMD ["socat", "-T", "5", "TCP-LISTEN:80,reuseaddr,fork", "SYSTEM:/serve.sh"]
