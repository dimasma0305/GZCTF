FROM python:3.12-alpine

LABEL org.gzctf.keep="true"

COPY app.py /app/app.py

# No baked flag: the platform supplies the live flag at $GZCTF_FLAG_FILE
# (Docker: /flag bind-mount; K8s: /gzctf-flag/flag via the flag-pull sidecar).
# app.py reads $GZCTF_FLAG_FILE fresh per request; a missing file = warmup.
EXPOSE 5000
CMD ["python", "/app/app.py"]
