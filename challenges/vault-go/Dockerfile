FROM golang:1.23-alpine AS build
WORKDIR /src
COPY main.go .
RUN go mod init vault && CGO_ENABLED=0 go build -ldflags="-s -w" -o /vault .

FROM alpine:3.21
LABEL org.gzctf.keep="true"

# supervisor as PID 1 (see supervisord.conf) so the service can be restarted in
# place without the container exiting — the team's changes survive.
RUN apk add --no-cache supervisor

COPY --from=build /vault /vault
COPY supervisord.conf /etc/supervisord.conf

# No baked flag: the platform supplies the live flag at $GZCTF_FLAG_FILE
# (Docker: /flag bind-mount; K8s: /gzctf-flag/flag via the flag-pull sidecar).
# main.go reads $GZCTF_FLAG_FILE fresh per request; a missing file = warmup.
EXPOSE 8080
CMD ["supervisord", "-c", "/etc/supervisord.conf"]
