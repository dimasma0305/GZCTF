FROM alpine:3.21

# Spawned per tick with no long-running container holding it — keep it out of
# the host prune (label) so checks don't start failing to pull (InternalError).
LABEL org.gzctf.keep="true"

RUN apk add --no-cache curl

COPY check.sh /check.sh
RUN chmod +x /check.sh

# enochecker3 exit-code contract: 0=Ok, 1=Mumble, 2=Offline, 3=InternalError.
# Reads GZCTF_TARGET_IP, GZCTF_TARGET_PORT, GZCTF_FLAG from env.
ENTRYPOINT ["/check.sh"]
