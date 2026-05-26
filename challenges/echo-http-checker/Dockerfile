FROM alpine:3.21

# Keep this image out of the host's daily `docker image prune -af` — the
# checker has no long-running container holding it (it's spawned per tick),
# so an aggressive prune deletes it and every A&D check then fails to pull it
# (InternalError). The prune cron excludes label org.gzctf.keep=true.
LABEL org.gzctf.keep="true"

RUN apk add --no-cache curl

COPY check.sh /check.sh
RUN chmod +x /check.sh

# enochecker3 exit-code contract: 0=Ok, 1=Mumble, 2=Offline, 3=InternalError.
# Reads GZCTF_TARGET_IP, GZCTF_TARGET_PORT, GZCTF_FLAG from env (set by
# AdCheckerExecutor at run time).
ENTRYPOINT ["/check.sh"]
