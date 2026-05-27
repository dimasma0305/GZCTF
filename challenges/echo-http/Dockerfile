FROM alpine:3.21

# Keep out of the host's daily `docker image prune -af` (running team
# containers usually hold this, but label it too for safety). The prune cron
# excludes label org.gzctf.keep=true.
LABEL org.gzctf.keep="true"

# supervisor runs as PID 1 (see supervisord.conf): the service runs as a child,
# so it can crash / be restarted in place WITHOUT the container exiting — the
# team's filesystem changes survive. (Pulls python3 — the cost of supervisord.)
RUN apk add --no-cache socat supervisor

# No baked flag: the platform supplies the live flag at $GZCTF_FLAG_FILE
# (Docker: /flag bind-mount; K8s: /gzctf-flag/flag via the flag-pull sidecar).
# serve.sh reads $GZCTF_FLAG_FILE fresh per request; a missing file = warmup.
COPY serve.sh /serve.sh
RUN chmod +x /serve.sh
COPY supervisord.conf /etc/supervisord.conf

EXPOSE 80
CMD ["supervisord", "-c", "/etc/supervisord.conf"]
