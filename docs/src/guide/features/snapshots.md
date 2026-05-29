# Container Snapshots

In Attack & Defense games, each team gets its own running container per A&D challenge — a box they SSH into, patch, and (try to) defend. When the game ends those containers are torn down. **Container Snapshots** are this fork's mechanism for preserving the forensic record of what each team did to their box, so organizers can run writeups, settle disputes, and study patches after the event.

This is exclusive to this fork; upstream GZ::CTF has no A&D mode and therefore no snapshotting.

There are two distinct artifacts, captured at different times by different code paths:

| Artifact | What it is | When captured | Where stored | Provider |
|----------|-----------|---------------|--------------|----------|
| **End-of-game tarball** | A `docker commit` + `docker save` of the team's container, gzipped | Once, at game end | Blob storage, key `ad-snapshots/{gameId}/{participationId}-{challengeId}.tar.gz` | Docker only |
| **Per-round change manifest** | A deduped list of files the team touched, relative to the baseline image | Every round, while the game runs (deduped) | DB rows (`AdServiceSnapshot.ManifestJson`) | Docker + K8s |

The tarball is the heavyweight forensic deliverable (a full filesystem you can `docker load` and shell into). The manifests are a lightweight timeline of *which files changed when*, so you can diff a team's box between round X and round Y without storing a tarball per round.

See also [/guide/features/attack-defense](/guide/features/attack-defense) for the broader A&D model and [/config/appsettings](/config/appsettings) for storage configuration.

## The end-of-game tarball

When a game's `EndTimeUtc` passes, the A&D reconciler (`AdContainerManager`) walks every team service that still has a live container and, before destroying it, snapshots it — but only if the game's `AdAllowSnapshotDownload` setting is `true` and no snapshot was already taken.

The capture (`TrySnapshotAsync`) does three things, in order:

1. **Captures the filesystem diff first** via `docker diff` (`InspectChanges`) — the writable-layer changes versus the baseline image. This is the "what did the team change" data. It is trimmed to the first 3000 entries and stored as JSON on `AdTeamService.SnapshotChanges`. A failure here is logged but does not abort the snapshot.
2. **Commits the container to an image** (`docker commit`) named `ad-snapshot-{serviceId}:{gameId}`, with a commit comment recording the team and challenge.
3. **Exports + uploads** the image with `docker save`, gzips it (`CompressionLevel.Fastest`), and writes it to blob storage. The committed local image is then deleted (best-effort) — the tarball in blob storage is the deliverable.

```text
docker diff   →  SnapshotChanges (JSON, ≤3000 entries)
docker commit →  ad-snapshot-{serviceId}:{gameId}
docker save   →  gzip  →  blob  ad-snapshots/{gameId}/{participationId}-{challengeId}.tar.gz
```

The teardown runs each service under its per-service lock and re-checks the end condition inside the lock, so a snapshot can't race a concurrent self-reset or an extended game.

:::warning Docker only
The tarball path is Docker-only in this fork. On a Kubernetes deployment `TrySnapshotAsync` logs `"A&D snapshot skipped: Docker provider not registered (K8s deployment)"` and returns null — there is no image-layer save API to commit against. K8s deployments still get the per-round change manifests and, at game end, a final `SnapshotChanges` computed via `exec` (see below); they just don't get a downloadable filesystem tarball.
:::

### Restoring a tarball for forensics

The blob is a standard gzipped `docker save` archive. To inspect a team's final box:

```bash
# Download via the admin endpoint (see "Downloading" below), then:
gunzip -c ad-snapshot-team42-challenge7.tar.gz | docker load
# docker load prints the loaded image ref, e.g. ad-snapshot-13:1
docker run --rm -it ad-snapshot-13:1 /bin/sh
```

From there you have the team's actual filesystem — their patches, any attacker footholds, dropped files, modified binaries.

## The per-round change manifests

`AdSnapshotService` is a background service that polls every **30 seconds**. For each active game with at least one enabled A&D challenge it finds the latest round, then for every team service with a live container it captures a *changed-file manifest*.

The manifest is produced by the same diff machinery as the live admin view, `ComputeLiveChangesAsync`:

- **Docker:** `docker diff` (`InspectChanges`) against the baseline image — precise add / modify / delete (kind `1` / `0` / `2`). Ancestor directories are collapsed to leaf paths.
- **Kubernetes:** an `exec`'d `find / -xdev … -newer /proc/1 -type f` in the pod — an mtime heuristic for "files modified since the container started". No add/modify/delete distinction and it misses deletions, so every entry is reported as kind `0` (modified).

Both branches filter out runtime/churn noise so the manifest reflects deliberate team changes, not platform activity. The filtered-out categories are:

```text
flag mount (/flag, /gzctf-flag)
/tmp and /run
/var/log, /var/cache, /var/tmp, /var/run, package caches
/proc, /sys, /dev
Python __pycache__ and *.pyc
directories that only contain a changed file (ancestor dirs)
```

:::danger Footholds in filtered paths don't show
The change view is a filtered blacklist. An attacker foothold dropped into `/tmp`, `/run`, `/var/log`, etc. will **not** appear in the manifests or the change diff. For those, restore the tarball and inspect the filesystem directly (or use the in-browser shell on a live box).
:::

### Dedup and gating

Manifests are stored deduped: a new `AdServiceSnapshot` row is written only when the canonicalized changed-file set differs from that service's previous one. So storage tracks *distinct states*, not ticks — a team that stops touching files stops generating rows.

Two gates keep the scan cheap:

- **Per-round:** a service is scanned at most once per round (tracked in-memory by highest round number seen), so the expensive scan — on K8s, a whole-rootfs `find` per pod — doesn't re-run on every 30s poll.
- **Bounded parallelism:** at most **8** concurrent change scans across all active games, mirroring the SLA checker's bound.

Each row records the round it was captured in (`AdRoundId`), `CapturedAt`, and the canonical `ManifestJson` (`[{ "p": path, "k": kind }]`).

## Restarting vs. resetting a box

A frequent point of confusion: **restarting an A&D container does not preserve its filesystem.** There is one restart path, `RestartContainerAsync`, and it always **destroys the container and recreates it from the same challenge image** — the box reverts to baseline and gets a new IP. It backs both:

- **Player self-reset** — `POST /api/Game/{id}/Ad/Services/{adTeamServiceId}/Reset`. Gated by the per-challenge `AdAllowSelfReset` flag, the game-wide `AdResetCooldownMinutes` cooldown, and the game window (only while running).
- **Operator force-restart** — `POST /api/edit/games/{id}/ad/Services/{adTeamServiceId}/Restart` (game admin). Bypasses the player cooldown — for when a team's box is wedged and they can't recover it themselves.

:::info There is no "keep the filesystem" restart
Resetting/restarting reverts the box to the image; the team's patches are gone. The *only* thing that survives a wiped container is what was already captured into a snapshot. The end-of-game tarball captures the box's final state at teardown; the per-round manifests capture the timeline up to that point. If you need a team's mid-game state preserved, it has to be in a manifest before the reset — the live `docker diff` is computed fresh, so once the layer is gone, so is the evidence.
:::

Provider note: on K8s a reset/restart still destroys and recreates the pod from the image; there is no in-place state-retaining restart on either provider.

## Downloading snapshots

### Participant download (gated)

Players can pull **their own team's** end-of-game tarball, but only when all of these hold:

```text
GET /api/Game/{id}/Ad/Services/{adTeamServiceId}/Snapshot   (RequireUser)
```

- The caller is a member of the team that owns the service (else `403`).
- The game has ended (`now >= EndTimeUtc`) — never mid-game, even if a stale `SnapshotBlobKey` lingers from a prior, extended game (else `404 "Snapshot is only available after the game ends"`).
- A snapshot was actually taken — `SnapshotBlobKey` is set (else `404 "Snapshot not available (game still running, snapshot disabled, or snapshot failed)"`).
- The blob still exists in storage (else `404 "Snapshot blob is missing — may have been retained-out"`).

The whole participant path is gated by the game's `AdAllowSnapshotDownload` setting. That setting is consulted *at game end* to decide whether to capture the tarball at all — so if it was `false` when the game ended, no `SnapshotBlobKey` was ever written and the download 404s.

The response is the gzipped tarball as `application/gzip`, filename `ad-snapshot-team{participationId}-challenge{challengeId}.tar.gz`.

### Admin download and inspection (any team)

Game admins are not team-scoped — they can pull any team's snapshot and inspect changes. The admin endpoints live under `/api/edit/games/{id}/ad/…` (where `…` below stands for that prefix):

| Endpoint | Purpose |
|----------|---------|
| `GET …/Services/{adTeamServiceId}/Snapshot` | Download any team's tarball |
| `GET …/Services/{adTeamServiceId}/Snapshot/Changes` | The changed-file list. Prefers the post-game `SnapshotChanges`; falls back to a **live** `docker diff` (mid-game) or the latest stored manifest. `Live=true` flags an on-demand live read (mtime-based on K8s). Includes `FilteredCategories` so you know what's hidden. |
| `GET …/Services/{adTeamServiceId}/Snapshots` | The capture-point history (round + `CapturedAt` + file count) for time-diffing |
| `GET …/Services/{adTeamServiceId}/SnapshotDiff` | Diff two capture points — `Added` (touched in the later point only, i.e. new activity) and `Removed` (touched earlier only, e.g. reverted) |

Per-file content (current vs. baseline + unified diff) and the in-browser shell on live boxes round out the admin forensics surface.

## Storage and retention

The end-of-game tarballs live in whatever [blob storage](/config/appsettings) the deployment is configured with (the same `IBlobStorage` used for uploads). Tarball size scales with image size, so large challenge images produce large snapshots — budget storage accordingly.

Retention is governed per game by **`AdSnapshotRetentionDays`**:

| Value | Meaning |
|-------|---------|
| `null` (default) | Keep indefinitely |
| positive integer `N` | *Intended:* retain for `N` days after game end. **Not yet enforced** — the value is persisted and round-trips through export/import, but no cleanup job currently reads it to delete expired tarballs, so storage must be reclaimed manually. |

```csharp
// Game.cs
public bool AdAllowSnapshotDownload { get; set; } = true;   // capture + expose tarballs?
public int? AdSnapshotRetentionDays { get; set; }            // null = keep forever
```

Both settings are part of the game definition: they round-trip through game export/import and can be set in a `.gzevent` manifest (`SnapshotRetentionDays`) for repo-bound games, or edited in the admin game-info UI.

:::tip Set retention before the game ends
`AdAllowSnapshotDownload` is read at teardown to decide whether the tarball is even created — so toggle it before the game ends, not after. `AdSnapshotRetentionDays` is a stored policy value only — as of now no cleanup job reads it, so tarballs are never auto-expired regardless of the setting; it round-trips through export/import but does not yet affect tarball lifetime, and your forensic record stays until you remove it manually.
:::

:::warning "retained-out" 404s
If a download returns `"Snapshot blob is missing — may have been retained-out"`, the DB still has a `SnapshotBlobKey` but the underlying blob is gone from storage (manually or externally removed — note `AdSnapshotRetentionDays` is recorded but not yet enforced by any cleanup job, so there is no automatic expiry). The change manifests and `SnapshotChanges` in the DB are unaffected — only the downloadable tarball is gone.
:::

## Quick reference

- **Capture (tarball):** automatic, once, at game end — Docker only, gated by `AdAllowSnapshotDownload`.
- **Capture (manifests):** automatic, every 30s poll / once-per-round-per-service, deduped — Docker + K8s.
- **Player download:** `GET /api/Game/{id}/Ad/Services/{adTeamServiceId}/Snapshot` — own team, post-game, gated.
- **Admin:** `GET /api/edit/games/{id}/ad/Services/{adTeamServiceId}/Snapshot` plus `…/Snapshot/Changes`, `…/Snapshots`, `…/SnapshotDiff` — any team.
- **Restart/reset:** always reverts the box to its image; only snapshots survive a wipe.

Related: [/guide/features/attack-defense](/guide/features/attack-defense) · [/config/appsettings](/config/appsettings)
