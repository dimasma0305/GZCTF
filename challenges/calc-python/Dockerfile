FROM python:3.12-alpine

LABEL org.gzctf.keep="true"

COPY app.py /app/app.py

RUN echo 'flag{warmup-no-round-yet}' > /flag && chmod 644 /flag

EXPOSE 5000
CMD ["python", "/app/app.py"]
