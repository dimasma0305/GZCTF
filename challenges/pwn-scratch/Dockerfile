FROM golang:1.23-alpine AS build
WORKDIR /src
COPY main.go .
RUN go mod init pwnbox && CGO_ENABLED=0 go build -ldflags="-s -w" -o /app .

# Deliberately FROM scratch: no shell, no coreutils. The service runs fine,
# but the admin file-inspection / per-round snapshot features (which K8s
# implements via `exec ... find/head` in the pod) CANNOT run here — there's
# nothing to exec. This challenge exists to surface that downside of the
# exec-based design under a realistic, common image profile (static binary).
FROM scratch
LABEL org.gzctf.keep="true"
COPY --from=build /app /app
EXPOSE 9000
ENTRYPOINT ["/app"]
