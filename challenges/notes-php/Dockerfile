FROM php:8.3-cli

# Keep out of the host's daily `docker image prune -af` (the prune cron
# excludes label org.gzctf.keep=true).
LABEL org.gzctf.keep="true"

# supervisor as PID 1 (see supervisord.conf) so the service can be restarted in
# place without the container exiting — the team's changes survive.
RUN apt-get update \
    && apt-get install -y --no-install-recommends supervisor \
    && rm -rf /var/lib/apt/lists/*

COPY app.php /app/app.php
COPY supervisord.conf /etc/supervisord.conf

# No baked flag: the platform supplies the live flag at $GZCTF_FLAG_FILE
# (Docker: /flag bind-mount; K8s: /gzctf-flag/flag via the flag-pull sidecar).
# app.php reads $GZCTF_FLAG_FILE fresh per request; a missing file = warmup.
EXPOSE 8080
CMD ["supervisord", "-c", "/etc/supervisord.conf"]
