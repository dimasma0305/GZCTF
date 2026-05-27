FROM golang:1.23-alpine AS build
WORKDIR /src
COPY main.go .
RUN go mod init vault && CGO_ENABLED=0 go build -ldflags="-s -w" -o /vault .

FROM alpine:3.21
LABEL org.gzctf.keep="true"
COPY --from=build /vault /vault
# No baked flag: the platform supplies the live flag at $GZCTF_FLAG_FILE
# (Docker: /flag bind-mount; K8s: /gzctf-flag/flag via the flag-pull sidecar).
# main.go reads $GZCTF_FLAG_FILE fresh per request; a missing file = warmup.
EXPOSE 8080
ENTRYPOINT ["/vault"]
