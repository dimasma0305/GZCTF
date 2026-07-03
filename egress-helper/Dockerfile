# Prebuilt helper for AdEgressIsolationService. Every ~30s that service launches a fresh
# privileged container in the HOST netns to (re)apply the DOCKER-USER egress-containment
# ruleset for A&D/KotH challenge containers. iptables + ipset are not in bare alpine, so
# the old script ran `apk add` on EVERY pass — a live network fetch that adds latency and,
# if the apk mirror is unreachable mid-game, fails the pass and silently leaves the egress
# control unapplied. Baking the tools in makes each pass a fast, dependency-free rule apply.
FROM alpine:3.21
RUN apk add --no-cache iptables ipset
