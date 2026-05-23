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
