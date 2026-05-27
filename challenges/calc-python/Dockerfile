FROM python:3.12-alpine

LABEL org.gzctf.keep="true"

# supervisor as PID 1 (see supervisord.conf) so the service survives an in-place
# restart without the container exiting — the team's changes persist. Installed
# via pip so it reuses the image's existing python (no second interpreter).
RUN pip install --no-cache-dir supervisor

COPY app.py /app/app.py
COPY supervisord.conf /etc/supervisord.conf

# No baked flag: the platform supplies the live flag at $GZCTF_FLAG_FILE
# (Docker: /flag bind-mount; K8s: /gzctf-flag/flag via the flag-pull sidecar).
# app.py reads $GZCTF_FLAG_FILE fresh per request; a missing file = warmup.
EXPOSE 5000
CMD ["supervisord", "-c", "/etc/supervisord.conf"]
