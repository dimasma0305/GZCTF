FROM alpine:3.21

RUN apk add --no-cache curl

COPY check.sh /check.sh
RUN chmod +x /check.sh

# enochecker3 exit-code contract: 0=Ok, 1=Mumble, 2=Offline, 3=InternalError.
# Reads GZCTF_TARGET_IP, GZCTF_TARGET_PORT, GZCTF_FLAG from env (set by
# AdCheckerExecutor at run time).
ENTRYPOINT ["/check.sh"]
