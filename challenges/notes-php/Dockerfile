FROM php:8.3-cli

# Keep out of the host's daily `docker image prune -af` (the prune cron
# excludes label org.gzctf.keep=true).
LABEL org.gzctf.keep="true"

COPY app.php /app/app.php

# Default warmup flag — the platform rotates the live flag each tick into
# GZCTF_FLAG_FILE (/flag on Docker, /gzctf-flag/flag on K8s). app.php reads
# it fresh per request.
RUN echo 'flag{warmup-no-round-yet}' > /flag && chmod 644 /flag

EXPOSE 8080
CMD ["php", "-S", "0.0.0.0:8080", "/app/app.php"]
