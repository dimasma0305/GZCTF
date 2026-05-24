FROM alpine:3.21

RUN apk add --no-cache socat

# Default "warmup" flag — overwritten by the platform at round start (env
# GZCTF_FLAG) and rotated each tick via docker exec into /flag.
RUN echo 'flag{warmup-no-round-yet}' > /flag && chmod 644 /flag

COPY serve.sh /serve.sh
RUN chmod +x /serve.sh

EXPOSE 80

# socat keeps re-listening on 80, forks per connection, runs serve.sh.
# Reads /flag at request time so per-tick rotation is visible
# immediately — no caching, no reload-needed.
CMD ["socat", "-T", "5", "TCP-LISTEN:80,reuseaddr,fork", "SYSTEM:/serve.sh"]
