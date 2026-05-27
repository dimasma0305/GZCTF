FROM golang:1.23-alpine AS build
WORKDIR /src
COPY main.go .
RUN go mod init vault && CGO_ENABLED=0 go build -ldflags="-s -w" -o /vault .

FROM alpine:3.21
LABEL org.gzctf.keep="true"
COPY --from=build /vault /vault
RUN echo 'flag{warmup-no-round-yet}' > /flag && chmod 644 /flag
EXPOSE 8080
ENTRYPOINT ["/vault"]
