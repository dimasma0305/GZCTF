# Challenge Build Pipeline

GZ::CTF (this fork) can build a challenge's container image **from source** at import time — you ship a `Dockerfile` in the challenge package and the platform produces a tagged, ready-to-run image on the local Docker daemon. No registry round-trip, no manual `docker build`, no hand-copied image tags in `challenge.yaml`.

This is exclusive to this fork. Upstream GZ::CTF expects you to publish an image to a registry yourself and reference it by tag; here, omitting `containerImage` (or pointing it at a local path) is enough.

This page covers how images are auto-built, the build queue and job lifecycle, the review gate that defers untrusted code, and the admin build-audit surface.

See also: [/guide/authoring/challenge-yaml](/guide/authoring/challenge-yaml) for the package schema and [/guide/authoring/repo-bindings](/guide/authoring/repo-bindings) for syncing challenges straight from a Git repo.

## When a build fires

Every challenge import (tarball upload, GitHub import, or repo-binding scan) parses each `challenge.yaml`, then asks one question per container challenge: **does this imply an image build?** The decision is `ResolveBuildIntent` in `ChallengeImportService`, and it runs in this order:

1. **Not a container type** → no build (`BuildStatus = None`).
2. **`containerImage` is a local path** (`./src`, `./Dockerfile`, `./src/Dockerfile`, a bare `Dockerfile`, or anything ending in `/Dockerfile`) → resolve the build context and build it.
3. **`containerImage` is a gzcli template placeholder** (it contains `{{`, e.g. `"{{.slug}}:latest"`) → look for a Dockerfile in the conventional spot and build it.
4. **`containerImage` is omitted/empty but a Dockerfile exists in the conventional spot** → build it anyway (you clearly meant to ship a container; the platform fills in the tag).
5. **`containerImage` is a registry-style ref** (`nginx:alpine`, `ghcr.io/foo/bar:tag`) → no build, the runner pulls it as-is (`BuildStatus = NotApplicable`).

### The convention search

For cases 3 and 4 (template placeholder or empty image), the platform searches for a Dockerfile in this exact order, relative to the directory that holds `challenge.yaml`:

| Order | Probe path | Resulting build context | Dockerfile |
|-------|-----------|-------------------------|------------|
| 1 | `./src/Dockerfile` | `<package>/src` | `Dockerfile` |
| 2 | `./Dockerfile` | `<package>` | `Dockerfile` |

`./src/Dockerfile` wins when both exist. If neither is found and the image was a template placeholder, the challenge lands in `MissingDockerfile` with a diagnostic; if the image was simply empty, it stays `None` (manual, no auto-build).

When `containerImage` is an explicit local path, `ResolveBuildContext` maps it to a `(context, Dockerfile)` pair:

| Declared `containerImage` | Build context | Dockerfile |
|---------------------------|---------------|------------|
| `./src` | `<package>/src` | `Dockerfile` |
| `./Dockerfile` | `<package>` | `Dockerfile` |
| `./src/Dockerfile` | `<package>/src` | `Dockerfile` |

:::info
The build context is always confined to the challenge package directory. A declared path that resolves outside the package (e.g. `../../etc`) is rejected with `build context path '…' escapes the challenge package`. Symlinks in the context are skipped when the snapshot is copied, so a link to the host kubeconfig, A&D flags, or WireGuard keys can't be baked into an attacker's image.
:::

### A minimal buildable package

```text
my-challenge/
├── challenge.yaml
├── solver/
│   └── solve.py
└── src/
    └── Dockerfile
```

```yaml
# challenge.yaml — containerImage omitted entirely; src/Dockerfile is found by convention
name: Tower of Babel
category: Pwn
type: StaticContainer
description: Climb the stack.
container:
  exposePort: 9999
  memoryLimit: 128
flags:
  - flag{static_flag_here}
```

You can also be explicit, or use the gzcli template style (both build the same `src/Dockerfile`):

```yaml
container:
  containerImage: ./src           # explicit local path
# or
  containerImage: "{{.slug}}:latest"  # gzcli placeholder → convention search
```

## The deterministic `gzctf-auto/` image tag

The Docker builder (`DockerChallengeImageBuilder`) tar+gzips the build context and computes a SHA-256 over the **compressed bytes**. The image tag is derived from that hash:

```text
gzctf-auto/{gameId}/{slug}:{contextSha[..12]}
```

- `slug` is the challenge title normalized to lowercase, non-alphanumerics collapsed to `-` (e.g. `Tower of Babel` → `tower-of-babel`).
- The 12-hex-char suffix is the content digest.

```text
gzctf-auto/8/tower-of-babel:9f2c1ab44e07
```

Because the tag is content-addressed, **re-importing identical source reuses the existing image** instead of rebuilding. Before invoking `docker build`, the builder inspects the local tag; if it already exists, the build is skipped and the result is reported as `(cached)`.

:::tip
Edit any file in the build context and the digest changes, so the next import produces a new tag and a fresh build. No source change means no rebuild — this is why a repo-binding scan that finds no diffs is cheap.
:::

Built images are labelled `org.gzctf.keep=true` so a host-side `docker image prune -af --filter label!=org.gzctf.keep=true` won't delete them — important for A&D checker images that have no long-running container holding a reference.

After a successful build, the builder does **best-effort cleanup**: it untags older `gzctf-auto/{gameId}/{slug}:*` tags (previous content SHAs from earlier edits), then prunes dangling images. A cleanup failure never flips a successful build to failed.

## The review gate: who gets auto-built

This is the safety boundary. **Attacker-controlled Dockerfiles do not run on the shared build host until an admin has reviewed them.**

The import API carries an `AutoApprove` flag (`ChallengeImportOptions`). Auto-build only fires when `AutoApprove == true`:

| Entry point | Endpoint | `AutoApprove` | Build behavior |
|-------------|----------|---------------|----------------|
| Admin tarball/zip import | `POST /api/edit/games/{id}/challenges/import` | `true` | Builds immediately |
| Admin GitHub import | `POST /api/edit/games/{id}/challenges/importFromGitHub` | `true` | Builds immediately |
| Repo-binding scan | (background `RepoBindingDiscoveryService`) | `true` | Builds immediately |
| Public web submission | `POST /api/edit/games/{id}/challenges/submit` | `false` | **Deferred** until approval |

For a public submission (`AutoApprove == false`), the challenge is imported with `ReviewStatus = Pending`; the build intent is computed and the row reflects it, but **no job is enqueued**. The system logs `Challenge build deferred (pending review)` and the unreviewed Dockerfile is never executed. An admin builds it later via the per-challenge rebuild flow once they approve it.

Public submissions also pass a shape check (`ValidateSubmissionShape`) before reaching the review queue — they must include a non-empty `solver/` file, declare a flag source (`flags:` or `flagTemplate:`, unless the type uses the A&D engine), and have a buildable Dockerfile or explicit registry image. Admin and binding imports skip this check (trusted).

:::warning
The gate is purely about **building**. The path-traversal guards, the 1 GB extracted-size cap (decompression-bomb defense), and the symlink-skip in snapshot copy apply to *every* import path regardless of `AutoApprove`. The gate adds the rule that *running* a user-supplied Dockerfile waits for human review.
:::

## Build queue and job lifecycle

When an auto-build is warranted, the importer doesn't build inline. It snapshots the context to a fresh temp dir (`/tmp/gzctf-build-<guid>`, owned by the worker for cleanup) and enqueues a `ChallengeBuildJob` onto a bounded in-memory channel. `Enqueue` returns one of:

| `EnqueueResult` | Meaning | Effect on the challenge |
|-----------------|---------|-------------------------|
| `Enqueued` | Job accepted | `BuildStatus = Queued` |
| `AlreadyPending` | A build for this challenge id is already queued/running | Untouched — the existing job satisfies this re-import; snapshot discarded |
| `Rejected` | Channel full | `BuildStatus = Failed`, log "Build queue is full — try again in a moment." |

The status flip to `Queued` happens **only after** the queue accepts the job, so a row never sits in `Queued` without an actual job behind it. The dedup (`AlreadyPending`) is what stops a double-click on Rebuild — or a re-import while a build runs — from producing two Docker builds and two audit rows.

`ChallengeBuildQueueService` is a background service with a fixed pool of **2 workers** draining the channel. Per job:

1. Mark the challenge `Building` and create the `ChallengeBuildAudit` row up front (so both the live strip and the history table have something to render).
2. Call the builder, streaming output. Each output line is secret-scrubbed and appended to a buffer; roughly every **2 seconds** the current tail is flushed to `Challenge.LastBuildLog` so the admin modal can watch the log live.
3. On completion, write the terminal status, duration, log tail, and digest to both the audit row and the challenge.

### Status transitions

```text
                       ┌─────────────► Success   (image built/cached; ContainerImage set)
Queued ──► Building ───┤
                       └─────────────► Failed    (non-transient error)

(transient error, attempt < 3) ──► stays Building ──► AutoRetry re-enqueue
```

The full set of `BuildStatus` values surfaced on a challenge:

| `ChallengeBuildStatus` | When |
|------------------------|------|
| `None` | Not a build target / manual challenge |
| `NotApplicable` | Container type, but `containerImage` is a registry ref |
| `MissingDockerfile` | Container expected a Dockerfile that wasn't found |
| `Queued` | Job accepted, waiting for a worker |
| `Building` | Worker is running `docker build` (or a retry is pending) |
| `Success` | Image built or cache-hit; `ContainerImage` + `BuildImageDigest` set |
| `Failed` | Build failed (non-transient) or queue rejected |

On **success** the challenge's `ContainerImage` is overwritten with the built tag (the `gzctf-auto/...` local tag, or the registry tag if a push target is configured) and `BuildImageDigest` is set.

### Retries and timeouts

- The Docker build has a **5-minute** timeout; a registry push has a **10-minute** timeout.
- **Transient** failures — daemon-connection refused, connection reset, I/O timeout, EOF mid-build, 502/503/504, "temporarily unavailable" — retry up to **3 attempts** with **10s / 30s / 90s** backoff. During the backoff the challenge stays `Building` (no red flicker) and stays in the dedup set so a concurrent manual build can't slip in.
- **Non-transient** failures (Dockerfile syntax, missing base image, unauthorized) fail immediately on attempt 1 — no retry storm.

### Crash recovery

On app startup the service flips any row left in `Building` **or** `Queued` from a previous process lifetime to `Failed` with the message `Build interrupted by app restart.`, and writes a matching audit row. It also sweeps `/tmp/gzctf-build-*` snapshot dirs older than 1 hour. Nothing stays stuck on a yellow "Building" badge after a crash.

## Secret scrubbing

Build output is visible to admins via `Challenge.LastBuildLog` and `ChallengeBuildAudit.LogTail`. Before any line is buffered or flushed, `ScrubSecrets` redacts token-shaped strings to `***SCRUBBED***`:

| Pattern | Matches |
|---------|---------|
| `ghp_…` (36 chars) | GitHub personal access token |
| `github_pat_…` (82+ chars) | Fine-grained PAT |
| `gho_` / `ghs_` / `ghr_` (36 chars) | GitHub OAuth / server / refresh tokens |
| `AKIA…` (16 chars) | AWS access key id |

False positives are acceptable — a scrubbed log beats a leaked token. Log tails are also capped at 32 KiB.

## Optional registry push

By default, built images stay on the local daemon — fine for a single-host setup where the runner shares that daemon. If your runner pulls from a registry (e.g. a Kubernetes cluster), configure `BuildRegistryConfig` so the builder retags and pushes after each successful build:

| Field | Notes |
|-------|-------|
| `PushOnBuild` | Master switch. False → images stay local. |
| `Server` | Registry host, no scheme/trailing slash, e.g. `ghcr.io`, `registry.example.com:5000`. Only hard requirement when pushing. |
| `Namespace` | Optional single-segment prefix, e.g. `myorg`. |
| `Username` | Optional — anonymous pushes to a local insecure registry are valid. |
| `Password` | Stored XOR-obfuscated at rest; never persisted in plaintext. |

The pushed tag is `{Server}/{Namespace?}/gzctf-auto/{gameId}/{slug}:{sha}`. When a push is configured, the challenge's `ContainerImage` is set to the **registry** tag (that's what the runner pulls). A push failure (DNS, auth denied, timeout) flips the build to `Failed` with the reason in the log. See [/config/appsettings](/config/appsettings) for where this config lives.

```text
ghcr.io/myorg/gzctf-auto/8/tower-of-babel:9f2c1ab44e07
```

## Docker vs Kubernetes builders

| Runtime | Builder | Behavior |
|---------|---------|----------|
| Docker | `DockerChallengeImageBuilder` | Builds via `docker.sock` (`BuildImageFromDockerfileAsync`), tags locally, optional registry push. Fully supported. |
| Kubernetes | `K8sChallengeImageBuilder` | **Not supported in v1.** Returns a clear failure. |

Under the Kubernetes container provider, an auto-build fails immediately with:

```text
Auto-build is not supported in kubernetes runtime in v1. Publish the image to a
registry the cluster can pull from, then point container_image at that registry reference.
```

:::warning
On Kubernetes, don't rely on auto-build. Publish your image to a registry the cluster can pull from and set `containerImage` to that registry reference (case 5 above), which is `NotApplicable` for the build pipeline and pulled as-is.
:::

## Admin build observability

The admin Builds surface (under `/admin/builds`, served by `AdminController`) is the audit trail for every build. All endpoints require admin.

### History and live state

```bash
# Paginated audit history, newest first. Omit `status` for the full history,
# or filter by status / game.
curl -s 'https://gzctf.gzti.me/api/Admin/Builds?count=50&skip=0&status=Failed&gameId=8' \
  -H 'Cookie: GZCTF_Token=…'
```

`GET /api/Admin/Builds` returns rows with: challenge id/title, game id, enqueued/started/finished timestamps, `Trigger`, `Attempt`, `Status`, content `Digest`, the scrubbed `LogTail`, `ErrorMessage`, and `DurationMs`.

```bash
# Builds a worker is processing right now (in-memory; cleared on restart).
curl -s 'https://gzctf.gzti.me/api/Admin/Builds/InProgress' -H 'Cookie: GZCTF_Token=…'
```

`GET /api/Admin/Builds/InProgress` returns the live strip: audit id, challenge id, game id, slug, attempt, `Trigger`, and `StartedAtUtc` for each running build (used to render elapsed times).

The `Trigger` distinguishes how each build started:

| `BuildTrigger` | Source |
|----------------|--------|
| `Import` | Auto-fired by an import/scan |
| `Manual` | Operator clicked Rebuild |
| `AutoRetry` | Worker re-enqueued after a transient failure |
| `Bulk` | "Rebuild all failed" action |

### Triggering and managing builds

```bash
# Re-enqueue the build for whatever challenge owns this audit row
# (307-redirects to the per-challenge Rebuild flow, preserving POST).
curl -X POST 'https://gzctf.gzti.me/api/Admin/Builds/123/Reenqueue' -H 'Cookie: GZCTF_Token=…'

# Bulk-rebuild every Failed / MissingDockerfile challenge in a game.
# Skips challenges with no archive on file and any already-pending build.
curl -X POST 'https://gzctf.gzti.me/api/Admin/Games/8/BulkRebuild' -H 'Cookie: GZCTF_Token=…'
```

History cleanup and disk hygiene:

| Endpoint | Action |
|----------|--------|
| `DELETE /api/Admin/Builds/{auditId}` | Remove one audit row (history only; doesn't touch the image) |
| `POST /api/Admin/Builds/PruneFailed` | Delete every `Failed` audit row |
| `POST /api/Admin/Builds/BulkDelete` (body: `int[]` ids, ≤ 500) | Delete an explicit list of audit rows |
| `POST /api/Admin/Builds/PruneImages` | GC `gzctf-auto/*` images on the daemon that no live challenge references |

`PruneImages` builds its keep-set from current `ContainerImage` values (keeping both the registry-prefixed form and the bare local `gzctf-auto/` form, so the deterministic cache path survives), and only removes images whose tags are all unreferenced `gzctf-auto/*` tags.

:::tip
The per-challenge Rebuild button (`POST /api/edit/games/{id}/challenges/{cId}/rebuild`) re-extracts the challenge's stored archive (or re-fetches from its parent repo binding), snapshots, and enqueues with `Trigger = Manual`. This is the same flow `Reenqueue` and approval-after-review use, so all the dedup and fallback logic lives in one place. This is also how an admin builds a previously-deferred public submission after approving it.
:::

## Quick reference

- **Build target?** Omit `containerImage`, point it at a local path, or use a `{{…}}` placeholder. A registry ref means "pull, don't build."
- **Where the Dockerfile goes:** `./src/Dockerfile` (preferred) or `./Dockerfile`.
- **Tag:** `gzctf-auto/{gameId}/{slug}:{sha12}`, content-addressed and cached.
- **Trusted (admin/GitHub/binding) imports build immediately; public submissions defer until approval.**
- **Kubernetes runtime:** auto-build is unsupported — pre-publish to a registry.

Next: [/guide/authoring/challenge-yaml](/guide/authoring/challenge-yaml) · [/guide/authoring/repo-bindings](/guide/authoring/repo-bindings)
