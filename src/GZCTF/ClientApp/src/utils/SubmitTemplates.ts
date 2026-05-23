import JSZip from 'jszip'

/**
 * Two ready-to-edit challenge templates exposed as Download buttons on
 * the user-submit page. Generated client-side via JSZip — no backend
 * round-trip, no static asset to deploy. Each template carries the
 * minimum a `ChallengeImportService.ImportFromArchiveAsync` upload
 * needs (a `challenge.yml` at the archive root, plus the matching
 * `src/Dockerfile` for container types) so the participant can edit
 * the values and submit immediately.
 */

const STATIC_CONTAINER_YAML = `name: "My Challenge"
author: "you"
type: "StaticContainer"
category: "Web"
description: |
  Markdown is supported.

  \`\`\`
  nc {{ .host }} 1337
  \`\`\`
flags:
  - "flag{replace_me_static}"
container:
  containerImage: "./src/Dockerfile"   # auto-built from the Dockerfile below
  memoryLimit: 256
  cpuCount: 1
  exposePort: 1337
`

const DYNAMIC_CONTAINER_YAML = `name: "My Dynamic Challenge"
author: "you"
type: "DynamicContainer"
category: "Pwn"
description: |
  Each team gets a unique flag — substitute it into your service
  via the GZCTF_FLAG environment variable inside the container.

  \`\`\`
  nc {{ .host }} 1337
  \`\`\`
container:
  containerImage: "./src/Dockerfile"
  memoryLimit: 256
  cpuCount: 1
  exposePort: 1337
  flagTemplate: "flag{[GUID]}"           # [GUID] is replaced per-team
`

const STATIC_DOCKERFILE = `# Single-instance container. Players share one running copy.
FROM alpine:3.20

WORKDIR /app
COPY ./flag.txt /flag.txt
COPY ./serve.sh /app/serve.sh
RUN chmod +x /app/serve.sh

# Replace this with your actual service.
CMD ["/app/serve.sh"]
`

const STATIC_SERVE_SH = `#!/bin/sh
# Trivial example: echo the flag to anyone who connects.
# Replace with your real challenge logic + port binding.
nc -l -k -p 1337 -e cat /flag.txt
`

const STATIC_FLAG = `flag{replace_me_static}
`

const DYNAMIC_DOCKERFILE = `# Per-team container. The platform injects a unique flag via the
# GZCTF_FLAG env var on container start. Bake your service so it
# reads from that variable instead of a static file.
FROM alpine:3.20

WORKDIR /app
COPY ./serve.sh /app/serve.sh
RUN chmod +x /app/serve.sh

# Replace this with your actual service.
CMD ["/app/serve.sh"]
`

const DYNAMIC_SERVE_SH = `#!/bin/sh
# The platform sets GZCTF_FLAG per-team; never hard-code a flag here.
echo "Your flag is in \\$GZCTF_FLAG — find a way to read it."
nc -l -k -p 1337 -e sh -c 'echo "GZCTF_FLAG is set in the container env — exploit me."'
`

const DIST_README = `Put any player handouts in this dir.
Reference them from challenge.yml with:

  provide: "./dist/handout.zip"

The file is shown as a downloadable attachment on the challenge page.
`

const SOLVER_README = `# Solver

Drop your working solution here so admins (and your future self) can
verify the challenge still works after edits.

A solver should:
- Connect to the live service at \`{host}:{port}\` (or read the
  attachment from \`dist/\`)
- Reach the flag end-to-end
- Print it to stdout

Run locally during development against your test container:

    ./solve.py 127.0.0.1 1337
`

const STATIC_SOLVER = `#!/usr/bin/env python3
"""Solution for the Static Container challenge.
Replace the body with your real exploit / interaction logic."""
import socket, sys

def main(host: str, port: int) -> None:
    with socket.create_connection((host, port), timeout=5) as s:
        data = s.recv(4096)
        print(data.decode(errors='replace'))

if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else '127.0.0.1',
         int(sys.argv[2]) if len(sys.argv) > 2 else 1337)
`

const DYNAMIC_SOLVER = `#!/usr/bin/env python3
"""Solution for the Dynamic Container challenge.
Each team gets a per-instance flag injected via GZCTF_FLAG inside the
container — your solver must extract it from the running service,
not from a static file."""
import socket, sys

def main(host: str, port: int) -> None:
    with socket.create_connection((host, port), timeout=5) as s:
        # Replace with your real exploit. The flag is reachable
        # *inside* the per-team container; this stub just prints
        # whatever the service emits on connect.
        data = s.recv(4096)
        print(data.decode(errors='replace'))

if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else '127.0.0.1',
         int(sys.argv[2]) if len(sys.argv) > 2 else 1337)
`

const README = `# {{slug}} — GZCTF challenge template

Edit the files in this folder, then zip the whole directory and
upload it on /games/<id>/submit.

Layout:
  challenge.yml       — challenge metadata (REQUIRED)
  src/Dockerfile      — container build (REQUIRED for *Container types)
  src/                — anything else your container needs
  dist/               — files handed to players (optional)
  solver/             — your working solution (used by admins to verify)
`

/**
 * Build a Blob containing the static-container template.
 */
export async function buildStaticContainerTemplate(): Promise<Blob> {
  const zip = new JSZip()
  zip.file('challenge.yml', STATIC_CONTAINER_YAML)
  zip.file('src/Dockerfile', STATIC_DOCKERFILE)
  zip.file('src/serve.sh', STATIC_SERVE_SH)
  zip.file('src/flag.txt', STATIC_FLAG)
  zip.file('dist/.gitkeep', '')
  zip.file('dist/README.md', DIST_README)
  zip.file('solver/solve.py', STATIC_SOLVER)
  zip.file('solver/README.md', SOLVER_README)
  zip.file('README.md', README)
  return zip.generateAsync({ type: 'blob', compression: 'DEFLATE' })
}

/**
 * Build a Blob containing the dynamic-container template.
 */
export async function buildDynamicContainerTemplate(): Promise<Blob> {
  const zip = new JSZip()
  zip.file('challenge.yml', DYNAMIC_CONTAINER_YAML)
  zip.file('src/Dockerfile', DYNAMIC_DOCKERFILE)
  zip.file('src/serve.sh', DYNAMIC_SERVE_SH)
  zip.file('dist/.gitkeep', '')
  zip.file('dist/README.md', DIST_README)
  zip.file('solver/solve.py', DYNAMIC_SOLVER)
  zip.file('solver/README.md', SOLVER_README)
  zip.file('README.md', README)
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
  // Free memory; small delay so click handler reads the href first.
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}
