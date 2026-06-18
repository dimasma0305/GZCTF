# GZCTF A&D "bring your own container" tunnel — one static binary, two modes.
#
#   relay : launched by GZCTF on the per-team challenge bridge (the team's
#           service endpoint the checker/attackers hit).
#   agent : run by the team next to their own service; dials outbound to GZCTF.
#
# Built locally by docker compose (see the byoc-relay service) and tagged
# gzctf/byoc-relay:latest so it is present on the shared Docker daemon for GZCTF
# to launch as relays. The same image is what the team runs as the agent.

FROM golang:1.25-alpine AS build
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY main.go ./
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /gzctf-byoc .

FROM alpine:3.21
# ca-certificates: the agent dials wss:// and must verify GZCTF's TLS cert.
# tini: PID 1 for clean signal handling.
RUN apk add --no-cache ca-certificates tini && rm -rf /var/cache/apk/*
COPY --from=build /gzctf-byoc /usr/local/bin/gzctf-byoc
ENTRYPOINT ["/sbin/tini", "--", "/usr/local/bin/gzctf-byoc"]
