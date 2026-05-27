FROM alpine:3.21

LABEL org.gzctf.keep="true"

RUN apk add --no-cache curl

COPY check.sh /check.sh
RUN chmod +x /check.sh

# enochecker3 exit-code contract: 0=Ok, 1=Mumble, 2=Offline, 3=InternalError.
ENTRYPOINT ["/check.sh"]
