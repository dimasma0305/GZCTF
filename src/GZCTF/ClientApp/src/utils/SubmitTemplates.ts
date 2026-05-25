import JSZip from 'jszip'

/**
 * Downloadable challenge templates for the user-submit page.
 *
 * **Source of truth**: these are the verbatim
 * `internal/template/templates/others/event-template/.example/*`
 * trees from https://github.com/dimasma0305/gzcli — the same shapes
 * `gzcli init challenge` scaffolds. Mirrored here as TypeScript
 * string constants so the download is generated client-side via
 * JSZip (no backend round-trip, no static asset to deploy).
 *
 * If gzcli's upstream templates change, sync these strings to match.
 * Do not invent fields — the gzcli schema is authoritative.
 */

// ---------------------------------------------------------------------------
// static-attachment
// ---------------------------------------------------------------------------

const STATIC_ATTACHMENT_YAML = `# yaml-language-server: $schema=https://raw.githubusercontent.com/dimasma0305/gzcli/refs/heads/main/internal/template/templates/others/ctf-template/.gzctf/challenge.schema.yaml

name: "static-attachment"
author: "dimas"

# support markdown & html tags
description: |
  Example static attachment

type: "StaticAttachment" # don't touch this value
value: 1000 # don't touch this value

flags:
  - "flag{testing}"

provide: "./dist"
`

const STATIC_ATTACHMENT_FLAG = `flag{testing}
`

const STATIC_ATTACHMENT_SOLVER = `# example solver
`

const STATIC_ATTACHMENT_DIST_GITIGNORE = ``

// ---------------------------------------------------------------------------
// dynamic-container
// ---------------------------------------------------------------------------

const DYNAMIC_CONTAINER_YAML = `# yaml-language-server: $schema=https://raw.githubusercontent.com/dimasma0305/gzcli/refs/heads/main/internal/template/templates/others/ctf-template/.gzctf/challenge.schema.yaml

name: "dynamic-container"
author: "test"

# support markdown & html tags
description: |
  Testing dynamic container

type: "DynamicContainer" # don't touch this value
value: 1000 # don't touch this value

provide: "./dist"

container:
  flagTemplate: "FLAG{ini_test_flag_[TEAM_HASH]}"
  memoryLimit: 512
  cpuCount: 3
  storageLimit: 512
  exposePort: 8011
  enableTrafficCapture: true
`

const DYNAMIC_DOCKERFILE = `FROM python:3.9-alpine

RUN apk update && apk add socat

RUN adduser -D -u 1001 -s /bin/bash ctf

RUN mkdir /home/ctf/chall

COPY ./requirements.txt /home/ctf/chall
RUN pip3 install -r /home/ctf/chall/requirements.txt

RUN mkdir /home/ctf/chall/src

COPY ./chall.py /home/ctf/chall/src
COPY ./run.sh /home/ctf/chall/src

RUN chown -R root:root /home/ctf/chall
RUN chmod -R 555 /home/ctf/chall
USER ctf
WORKDIR /home/ctf/chall/src

CMD ["./run.sh"]
`

const DYNAMIC_RUN_SH = `#!/bin/sh

export FLAG=\${GZCTF_FLAG}
socat tcp-l:8011,reuseaddr,fork exec:"python3 chall.py"
`

const DYNAMIC_CHALL_PY = `# flag in env
print(__import__('os').popen('env').read())
`

const DYNAMIC_REQUIREMENTS = ``

const DYNAMIC_DOCKER_COMPOSE = `services:
  example:
    build: .
    restart: on-failure
    ports:
      - 8011:8011
    deploy:
      resources:
        limits:
          cpus: "0.5"
          memory: "256M"
        reservations:
          cpus: "0.25"
          memory: "128M"
`

const DYNAMIC_DIST_GITIGNORE = ``

const DYNAMIC_SOLVER = `# example solver
`

// ---------------------------------------------------------------------------
// attack-defense
// ---------------------------------------------------------------------------

const AD_CHALLENGE_YAML = `# yaml-language-server: $schema=https://raw.githubusercontent.com/dimasma0305/gzcli/refs/heads/main/internal/template/templates/others/ctf-template/.gzctf/challenge.schema.yaml

name: "attack-defense"
author: "test"

# support markdown & html tags
description: |
  Example Attack & Defense service. The platform plants a fresh flag into
  /flag every tick; your service must expose it to whoever holds the
  intended capability, and defenders patch the bug without breaking the
  checker.

type: "AttackDefense" # don't touch this value
value: 1000 # don't touch this value

# A&D reuses the container block for the per-team SERVICE image + port.
# containerImage omitted → platform auto-builds ./src/Dockerfile.
container:
  exposePort: 80
  memoryLimit: 256
  cpuCount: 1
  storageLimit: 256

# A&D per-challenge knobs (this service's own properties). All optional.
# Event-wide policy — tick length, flag lifetime, reset cooldown, snapshot
# download — lives in the game's settings (admin → game → Info), not here.
ad:
  # Checker image (enochecker3 exit-code contract: 0 Ok / 1 Mumble /
  # 2 Offline / 3 InternalError). Omit to fall back to a TCP-reachability
  # probe. A local ./checker path is NOT auto-built yet — push the checker
  # to a registry and uncomment the ref below.
  # checkerImage: "ghcr.io/your-org/attack-defense-checker:latest"
  # Reach the public internet? Default true (open). Set false to sandbox.
  allowEgress: true
  allowSelfReset: true
`

const AD_DOCKERFILE = `FROM alpine:3.21

RUN apk add --no-cache socat

# Warmup flag — the platform overwrites /flag every tick via docker exec.
# Read the flag from /flag at request time (path is also in GZCTF_FLAG_FILE).
# There is NO GZCTF_FLAG env var for A&D services: an env is frozen at
# container start and would go stale after the first rotation.
RUN echo 'flag{warmup-no-round-yet}' > /flag && chmod 644 /flag

COPY serve.sh /serve.sh
RUN chmod +x /serve.sh

EXPOSE 80

# socat forks per connection; serve.sh reads /flag fresh each request so
# the per-tick rotation is visible immediately (no caching).
CMD ["socat", "-T", "5", "TCP-LISTEN:80,reuseaddr,fork", "SYSTEM:/serve.sh"]
`

const AD_SERVE_SH = `#!/bin/sh
# Toy vulnerable service: echoes /flag to anyone who asks. Replace this
# with your real service — the only platform contract is that the round's
# flag lives at /flag (path also exposed as \\$GZCTF_FLAG_FILE), refreshed
# every tick. Defenders patch the bug; attackers exploit it to read /flag.
while IFS= read -r line; do
    line="\${line%$'\\r'}"
    [ -z "$line" ] && break
done

flag="$(cat /flag 2>/dev/null || echo 'no flag yet')"
body="flag is: \${flag}
"
printf 'HTTP/1.1 200 OK\\r\\n'
printf 'Content-Type: text/plain\\r\\n'
printf 'Content-Length: %d\\r\\n' "\${#body}"
printf 'Connection: close\\r\\n'
printf '\\r\\n'
printf '%s' "$body"
`

const AD_CHECKER_DOCKERFILE = `FROM alpine:3.21

RUN apk add --no-cache curl

COPY check.sh /check.sh
RUN chmod +x /check.sh

# enochecker3 exit-code contract: 0 Ok / 1 Mumble / 2 Offline / 3 InternalError.
# The platform runs this image once per (team, tick) with the target +
# planted flag in the environment.
ENTRYPOINT ["/check.sh"]
`

const AD_CHECK_SH = `#!/bin/sh
# Reference checker for the example echo service. The platform sets:
#   GZCTF_TARGET_IP / GZCTF_TARGET_PORT  -> the team's service
#   GZCTF_FLAG                           -> the flag planted THIS tick
#   GZCTF_ROUND / GZCTF_TEAM_ID          -> context (unused here)
#
# Exit code -> check status:
#   0 Ok       flag retrieved as planted
#   1 Mumble   service up but flag wrong / missing
#   2 Offline  TCP refused / timeout
#   3 InternalError  checker bug / missing env
set -u

[ -z "\${GZCTF_TARGET_IP:-}" ] && { echo "no target ip" >&2; exit 3; }
[ -z "\${GZCTF_TARGET_PORT:-}" ] && { echo "no target port" >&2; exit 3; }

body="$(curl -sS --max-time 5 "http://\${GZCTF_TARGET_IP}:\${GZCTF_TARGET_PORT}/" 2>&1)"
rc=$?
case "$rc" in
    0) ;;
    6|7|28|56) echo "offline: $body" >&2; exit 2 ;;
    *) echo "offline (curl $rc): $body" >&2; exit 2 ;;
esac

# No flag context (warmup) -> reachability only.
[ -z "\${GZCTF_FLAG:-}" ] && exit 0

case "$body" in
    *"\${GZCTF_FLAG}"*) exit 0 ;;
    *) echo "flag missing from response" >&2; exit 1 ;;
esac
`

const AD_SOLVER = `# Example A&D exploit.
#
# In Attack & Defense your "solver" is the exploit you run against OTHER
# teams' instances of this service each tick, then submit the captured
# flags via the API (see the in-game Toolkit -> "How to submit").
#
#   import requests
#   flag = requests.get(f"http://{target_ip}/", timeout=5).text
#   # POST flag to /api/Game/{id}/Ad/Submit with your Bearer token
`

// ---------------------------------------------------------------------------
// Build / download helpers
// ---------------------------------------------------------------------------

/**
 * Static attachment template — no container, no build. The player
 * downloads whatever lives in `dist/`; flag is matched server-side
 * from the `flags:` list.
 */
export async function buildStaticAttachmentTemplate(): Promise<Blob> {
  const zip = new JSZip()
  zip.file('challenge.yml', STATIC_ATTACHMENT_YAML)
  zip.file('src/flag.txt', STATIC_ATTACHMENT_FLAG)
  zip.file('dist/.gitignore', STATIC_ATTACHMENT_DIST_GITIGNORE)
  zip.file('solver/solve.py', STATIC_ATTACHMENT_SOLVER)
  return zip.generateAsync({ type: 'blob', compression: 'DEFLATE' })
}

/**
 * Dynamic container template — one container per team, flag injected
 * via the GZCTF_FLAG env var (no `flags:` block + a `flagTemplate`).
 */
export async function buildDynamicContainerTemplate(): Promise<Blob> {
  const zip = new JSZip()
  zip.file('challenge.yml', DYNAMIC_CONTAINER_YAML)
  zip.file('src/Dockerfile', DYNAMIC_DOCKERFILE)
  zip.file('src/run.sh', DYNAMIC_RUN_SH)
  zip.file('src/chall.py', DYNAMIC_CHALL_PY)
  zip.file('src/requirements.txt', DYNAMIC_REQUIREMENTS)
  zip.file('src/docker-compose.yml', DYNAMIC_DOCKER_COMPOSE)
  zip.file('dist/.gitignore', DYNAMIC_DIST_GITIGNORE)
  zip.file('solver/solve.py', DYNAMIC_SOLVER)
  return zip.generateAsync({ type: 'blob', compression: 'DEFLATE' })
}

/**
 * Attack & Defense template — a persistent per-team service plus a
 * checker. No `flags:` / `flagTemplate:`: the platform plants a fresh
 * flag into `/flag` every tick and rotates it. Ships the vuln service
 * under `src/`, a reference checker under `checker/`, and an exploit
 * stub under `solver/`.
 */
export async function buildAttackDefenseTemplate(): Promise<Blob> {
  const zip = new JSZip()
  zip.file('challenge.yml', AD_CHALLENGE_YAML)
  zip.file('src/Dockerfile', AD_DOCKERFILE)
  zip.file('src/serve.sh', AD_SERVE_SH)
  zip.file('checker/Dockerfile', AD_CHECKER_DOCKERFILE)
  zip.file('checker/check.sh', AD_CHECK_SH)
  zip.file('solver/solve.py', AD_SOLVER)
  return zip.generateAsync({ type: 'blob', compression: 'DEFLATE' })
}

/**
 * Trigger a browser download for the given blob.
 */
export function downloadBlob(blob: Blob, filename: string): void {
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = filename
  document.body.appendChild(a)
  a.click()
  document.body.removeChild(a)
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}
