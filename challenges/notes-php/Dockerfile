FROM php:8.3-cli

# Keep out of the host's daily `docker image prune -af` (the prune cron
# excludes label org.gzctf.keep=true).
LABEL org.gzctf.keep="true"

COPY app.php /app/app.php

# No baked flag: the platform supplies the live flag at $GZCTF_FLAG_FILE
# (Docker: /flag bind-mount; K8s: /gzctf-flag/flag via the flag-pull sidecar).
# app.php reads $GZCTF_FLAG_FILE fresh per request; a missing file = warmup.
EXPOSE 8080
CMD ["php", "-S", "0.0.0.0:8080", "/app/app.php"]
