# Importing & Repo Bindings

How organizers get challenges onto the platform — by hand for a one-off, or as a continuously-synced GitHub repository for a whole event. This page covers the import endpoints, the per-game submission gate, and the repo-binding poller that turns a tree of `.gzevent` manifests into live games.

:::tip
If you are authoring the challenges themselves, start with [Challenge YAML](/guide/authoring/challenge-yaml) and the [Templates](/guide/authoring/templates) — this page assumes you already have `challenge.yml` files in hand and want to get them imported at scale.
:::

## The four import paths

There are four ways a challenge (or a whole game) reaches the platform. They differ in **who can call them**, **whether the result is auto-approved or lands in a review queue**, and **whether it's a one-shot or a continuous sync**.

| Path | Endpoint | Who | Approval | Shape |
|---|---|---|---|---|
| Admin tarball import | `POST /api/Edit/Games/{id}/Challenges/Import` | Game admin | Auto-approved (`Active`) | One-shot |
| Public web submit | `POST /api/Edit/Games/{id}/Challenges/Submit` | Any logged-in user | Lands `Pending` review | One-shot |
| One-shot GitHub import | `POST /api/Edit/Games/{id}/Challenges/ImportFromGitHub` | Game admin | Auto-approved | One-shot |
| Game package import | `POST /api/Edit/Games/Import` | Admin | — (whole game) | One-shot |
| **Repo binding** | registered in admin UI | Admin | Auto-approved | **Continuous** |

The repo-binding poller is the scalable option and gets its own section below. The one-shot endpoints are described first.

### (a) Admin tarball import — auto-approved

A game admin uploads a single-challenge tarball; the resulting challenge(s) are imported **and approved in one shot** (they go straight to `Active` and are visible to participants once enabled).

```bash
curl -X POST "https://PUBLIC_ENTRY/api/Edit/Games/42/Challenges/Import" \
  -H "Cookie: GZCTF_Token=<admin-session>" \
  -F "archive=@my-challenge.tar"
```

The endpoint is gated by game-admin permission and passes `AutoApprove: true` into the import service. There is **no per-game opt-in** required for this path — being a game admin is the authorization.

### (b) Public web submit — gated, lands Pending

Any logged-in user can submit a single-challenge tarball, **but only if the game has user submissions enabled**. This is the path the [web upload templates](/guide/authoring/templates) point at (`/games/<id>/submit`).

```bash
curl -X POST "https://PUBLIC_ENTRY/api/Edit/Games/42/Challenges/Submit" \
  -H "Cookie: GZCTF_Token=<user-session>" \
  -F "archive=@my-challenge.tar"
```

Behaviour:

- The game's **`AllowUserSubmissions`** flag must be on (admin → game → Info). When it is off, the endpoint returns **`403 Forbidden`** with `"User submissions are disabled for this game."` and the submit page is disabled in the UI.
- On success the challenge is imported with `AutoApprove: false`, so it lands with **`ChallengeReviewStatus.Pending`** and is **hidden from participants** until an admin approves it via the review queue (`GET /api/Edit/Games/{id}/PendingChallenges`, then `POST .../Challenges/{cId}/Approve`).

:::info
**A&D and KotH challenges carry no author flag.** For these types the platform plants and rotates the flag itself (A&D) or scores a planted control token (KotH), so a submitted `AttackDefense` / `KingOfTheHill` archive does not need a `flags:` list to pass import. See [Attack & Defense](/guide/features/attack-defense) and [King of the Hill](/guide/features/king-of-the-hill) for the flag/scoring flow.
:::

### (c) One-shot import from GitHub

A game admin can pull challenges directly from a GitHub location (repo URL + optional ref + optional subpath) without registering a persistent binding. Results are **auto-approved**.

```bash
curl -X POST "https://PUBLIC_ENTRY/api/Edit/Games/42/Challenges/ImportFromGitHub" \
  -H "Cookie: GZCTF_Token=<admin-session>" \
  -H "Content-Type: application/json" \
  -d '{
        "repoUrl": "https://github.com/org/ctf-2026",
        "ref": "main",
        "subpath": "final",
        "gitHubToken": "ghp_xxx"
      }'
```

The URL/ref/subpath are validated by the GitHub locator; an unparseable URL returns `400`. The token is optional (only needed for private repos) and is used for that single pull — it is **not** persisted. For an ongoing sync, register a binding instead.

### (d) Bulk game-package import

`POST /api/Edit/Games/Import` takes a full exported **game ZIP** (the counterpart to the game export endpoint) and rebuilds the entire game. Admin-only.

```bash
curl -X POST "https://PUBLIC_ENTRY/api/Edit/Games/Import" \
  -H "Cookie: GZCTF_Token=<admin-session>" \
  -F "file=@game-42-export.zip"
```

It accepts only `.zip` files with a zip content type and returns the new game ID on success.

### Size limits (from the code)

These limits are enforced server-side. Exceeding them returns `400` before anything is unpacked.

| Path | Limit | Source |
|---|---|---|
| Challenge `Submit` / `Import` tarball | **64 MB** (`MaxTarballBytes = 64L * 1024 * 1024`) | `RequestSizeLimit` + explicit length check |
| Game package `Games/Import` ZIP | **512 MB** | explicit `case > 512 * 1024 * 1024` |

:::warning
The 64 MB tarball ceiling is per challenge archive. Large container challenges should ship their build context (`src/`, `Dockerfile`) — not a pre-built image — and put only player-facing files in `dist/`. If your archive is bigger than 64 MB, prefer a repo binding (which clones via git, not a single multipart upload).
:::

## Repo bindings

A **repo binding** is a platform-level registration of a GitHub repository. A background poller keeps it in sync: it clones the repo, walks the tree for every `.gzevent` manifest, turns each manifest into a **Game**, and imports every `challenge.yml` under that event root. This is the path for running a whole event — or many events — out of one repo.

A binding is distinct from a per-game repo *watch*: a watch updates one already-existing game, whereas a binding **creates games automatically** and lives at the platform level.

### What you register

| Field | Meaning | Default / clamp |
|---|---|---|
| `RepoUrl` | GitHub repository URL (unique per binding) | required |
| `Ref` | Branch / tag / ref to track | optional (repo default) |
| `GitHubTokenEncrypted` | PAT, **encrypted at rest** — required for private repos, null for public | optional |
| `IntervalSeconds` | Re-scan cadence | **default 60**, clamped to **[60, 86400]** |
| `Status` | `Active` polls, `Paused` skips | `Active` |
| `PushOnEdit` | Serialize admin-approved edits back to the repo | off |

The PAT is stored encrypted via ASP.NET DataProtection and is `[JsonIgnore]`d, so it is never returned by the API.

### How the poller works

The `RepoBindingScanService` background worker ticks **every 30 seconds**. On each tick it:

1. Runs a **stale-activity watchdog** — clears the live `CurrentActivity` field on any binding whose scan hasn't completed in 15 minutes (recovers a UI that's stuck on "Syncing…" after a mid-fetch crash).
2. Selects bindings that are `Active` and **due** (`NextScanUtc` is null or in the past), oldest first, **at most 8 per tick** (`Take(8)`) to bound GitHub work.
3. Scans each due binding, then **always advances `NextScanUtc`** by the clamped interval — even on failure — so a permanently-broken repo can't hot-loop the poller.

So a binding with the default `IntervalSeconds = 60` is re-scanned roughly once a minute (subject to the 30 s tick granularity and the 8-per-tick fan-out cap).

### Persistent shallow clone + SHA short-circuit

Each binding keeps a **persistent shallow git checkout** at `/app/repos/binding/{id}` (on the `gzctf-repos` Docker volume, so it survives container restarts):

- **First scan** = `git clone --depth 1` (one full payload).
- **Every later scan** = `git fetch --depth 1` (a small delta).

After the fetch, the worker compares the resolved commit SHA against the binding's `LastCommitSha`. **If they match and the scan was not forced, the import is skipped entirely** — the status row records `"No change since <sha7> — skipped import."`. On an unchanged repo this is the cheap (~150 ms) common case, which is why a 60 s cadence is affordable.

### "Scan now" (force)

The admin "Scan now" action calls the scan with `force = true`, which **bypasses the SHA short-circuit** and re-downloads + re-imports unconditionally even when git hasn't moved. Use it to recover from a partial scan or to re-run the import pipeline against an unchanged tree. Because discovery is idempotent (games are upserted by `(BindingId, EventManifestPath)`), forcing a re-scan is safe.

### What a scan does to each manifest

For every `.gzevent` found anywhere in the tree:

1. The manifest YAML is parsed into the event model (see below); a missing `title` or invalid YAML is recorded as a per-manifest failure and the scan moves on.
2. The Game is **upserted** — keyed on `(BindingId, manifestRelPath)`. A detached game with a matching title is *adopted* rather than duplicated (the delete-then-rebind recovery path).
3. Every `challenge.yml` under that **event root directory** is imported via the standard challenge-import pipeline with `AutoApprove: true`.

Sparse update semantics: on a re-scan, only the manifest fields that are actually present override the Game; omitted fields keep their current value, so a re-scan never resets hand-tuned settings.

Live progress is written to `CurrentActivity` as the scan walks (`"Syncing git checkout"`, `"Discovering .gzevent manifests"`, `"Processing event 2/5: final/.gzevent"`, …) and surfaced on the admin repo-bindings card. A summary like `games +1 ~0, challenges +12 ~3, failures 0` lands in `LastScanMessage` when it finishes.

## The `.gzevent` manifest

A `.gzevent` file at the root of an event directory tells the binding "this folder is one game." Every `challenge.yml` beneath it becomes a challenge in that game. All fields are optional except `title`; omitted fields keep platform defaults on create (and current values on update).

```yaml
# final/.gzevent  → becomes one Game; every challenge.yml under final/ is imported
title: "MyCTF 2026 Finals"
summary: "The on-site finals."
content: |
  Welcome to the finals. Read the rules carefully.
start: "2026-06-01T09:00:00+08:00"
end:   "2026-06-01T21:00:00+08:00"
hidden: false
practiceMode: true
acceptWithoutReview: false
inviteCode: "finals-2026"
teamMemberCountLimit: 4      # 0 = unlimited
containerCountLimit: 3
writeupRequired: false
writeupDeadline: "2026-06-08T21:00:00+08:00"
bloodBonus: 5000000          # packed first/second/third-blood bonus value

# Event-wide Attack & Defense knobs — shared by EVERY A&D/KotH challenge in
# this event, because a round spans the whole game. Omit the block to keep
# all platform defaults.
ad:
  tickSeconds: 60            # round/tick length              (default 60)
  flagLifetimeTicks: 5       # ticks a planted flag stays submittable (default 5)
  warmupSeconds: 1800        # grace before scoring begins    (default 1800)
  resetCooldownMinutes: 5    # min wait between team self-resets (default 5)
  allowSnapshotDownload: true        # post-game container snapshot (default true)
  snapshotRetentionDays: 30          # null = keep indefinitely
  getflagWindowFraction: 0.5         # getflag jitter as fraction of tick (default 0.5)
  minGracePeriodSeconds: 3           # delay after round start before getflag (default 3)
```

:::info
Dates use ISO-8601 and may carry a local offset (e.g. `+08:00`); the importer normalizes everything to UTC before persisting.
:::

### What the event-wide `ad:` block configures

The `ad:` block on the **manifest** carries only the **game-wide** A&D/KotH timing knobs — the ones that must be shared because a round spans the whole game (tick length, flag lifetime, warmup, reset cooldown, snapshot policy, and the getflag jitter/grace timing that are fractions of the tick).

It is **separate from the per-challenge `ad:` block** inside each `challenge.yml`, which carries that one service's own properties (`checkerImage`, `allowEgress`, `allowSelfReset`). Don't confuse the two. See [Attack & Defense](/guide/features/attack-defense) for the per-challenge block and the checker/flag flow.

:::warning
The KotH refresh interval and hold-points-per-tick are **not** carried in the manifest and are not exposed in the admin UI — they are currently fixed at their defaults. Tuning them today means editing the `Game` row directly (`KothRefreshTicks` / `KothHoldPointsPerTick`). See [King of the Hill](/guide/features/king-of-the-hill).
:::

## `ignore: true` — stop a challenge syncing

Deleting a challenge in the admin UI removes it from the platform but **not** from the repo, so the next scan would re-import (resurrect) it. To keep it gone, add `ignore: true` to its `challenge.yml`:

```yaml
name: "old-challenge"
type: "StaticAttachment"
ignore: true   # importer skips this challenge entirely — never created/updated
```

`ignore: true` makes the importer skip the challenge (no create, no update), so a one-time UI delete sticks. It does **not** delete an already-imported copy — remove that in the UI (after which it simply stops receiving updates). Drop the key to resume syncing.

:::tip
`value:` and any `visible:` / `enabled:` fields in `challenge.yml` are **ignored on import** — points and visibility are admin-controlled after review, not set by the repo.
:::

## `PushOnEdit` — write approved edits back to the repo

By default a binding is **read-only**: the platform imports from the repo and never writes to it. Enabling **`PushOnEdit`** reverses this for one specific flow — when an admin **approves** a user-submitted challenge, the platform serializes that challenge back into the binding repo as a fresh commit.

The written path mirrors the layout the scanner imports from:

```
{eventDir}/{category}/{slug}/challenge.yml
{eventDir}/{category}/{slug}/{attachment}     # local attachment, best-effort
```

where `eventDir` is the directory of the game's `EventManifestPath`, `category` is the challenge category, and `slug` is the normalized challenge title. After pushing, the challenge's `SourceYamlPath` is updated to that new path so subsequent edits push back to the same file. The push is **best-effort and fire-and-forget** — approval succeeds in the database even if the push later fails.

**Gates** (all must hold, or the approval is purely DB-side):

- the game has a `RepoBindingId` and a non-empty `EventManifestPath`,
- the binding has `PushOnEdit = true`,
- the binding has a stored token that decrypts successfully.

:::warning
**The PAT must have `Contents:write` scope** for push-back. A read-only token is fine for import but will fail the push.

Also note: regenerating the YAML **drops comments and reorders fields** per the serializer. Don't enable `PushOnEdit` on a repo whose `challenge.yml` files carry hand-edited comments you want to keep.
:::

## Binding health: `Status` and `TokenStatus`

Two fields, both surfaced on the admin repo-bindings card, tell you whether a binding is healthy.

**`Status`** — `Active` (polled every interval) or `Paused` (skipped by the poller).

**`TokenStatus`** — updated on every scan so the UI can distinguish a bad token from an unrelated scan failure:

| `TokenStatus` | Meaning |
|---|---|
| `NotConfigured` | No token stored — the public-repo path. |
| `Ok` | Token decrypted and was used successfully on the last scan. |
| `DecryptFailed` | Stored ciphertext could not be decrypted (e.g. the DataProtection key changed). The scan aborts with `"Stored token could not be decrypted."` |

:::info
A **private or missing repo**, or a **missing/expired token**, does not crash the poller — it surfaces as a concise scan warning on the binding row (a friendly hint plus git's stderr), logged once per tick as a single warning rather than a stack-trace flood. There is no `TokenStatus` value for "present but rejected," because a rejected token can't be told apart from a correct one without a probe; the actionable hint rides in `LastScanMessage` instead.
:::

## Worked example: the `gzctf-mix-demo` repo

A complete, public reference repo lives at **[github.com/dimasma0305/gzctf-mix-demo](https://github.com/dimasma0305/gzctf-mix-demo)** — point a binding at it to see the whole pipeline end to end. It's a **type × category coverage matrix**: one challenge for every `ChallengeType` crossed with every category (72 in total), so the scoreboard, the jeopardy/A&D/KotH kind switcher, and every category band render with real content.

### Layout

A single root `.gzevent` defines the game; each top-level directory is a category, and each leaf folder is one challenge:

```text
gzctf-mix-demo/
├── .gzevent                  # → one Game: "GZCTF — Type×Category Mix Demo"
├── AI/
│   ├── aandd-ai/             # type: AttackDefense  (image auto-built from ./src/Dockerfile)
│   │   ├── challenge.yaml
│   │   └── src/{Dockerfile,service.py}
│   ├── koth-ai-hill/         # type: KingOfTheHill
│   │   └── challenge.yaml
│   ├── ai-per-team-box/      # type: DynamicContainer
│   ├── ai-per-team-drop/     # type: DynamicAttachment (ships dist/)
│   ├── ai-sampler/           # type: StaticAttachment
│   └── ai-service/           # type: StaticContainer
├── Blockchain/  Crypto/  Forensics/  Hardware/  Misc/
├── Mobile/  PPC/  Pentest/  Pwn/  Reverse/  Web/   # same six types per category
└── ...
```

### The manifest

The repo's root `.gzevent` — note the event-wide `ad:` block that every A&D/KotH challenge in this game shares:

```yaml
title: "GZCTF — Type×Category Mix Demo"
start: "2026-05-28T00:00:00Z"
end:   "2026-06-30T00:00:00Z"
hidden: false
summary: "Showcase event with one challenge per (type, category) cell — 72 total."
acceptWithoutReview: true
practiceMode: true
teamMemberCountLimit: 0      # 0 = unlimited
containerCountLimit: 5
bloodBonus: 50
ad:
  tickSeconds: 60
  flagLifetimeTicks: 5
  warmupSeconds: 60
  resetCooldownMinutes: 5
```

### A challenge — A&D and KotH side by side

`AI/aandd-ai/challenge.yaml` — an Attack & Defense service whose image is **auto-built** from `./src/Dockerfile` (no `containerImage`):

```yaml
name: "A&D — AI"
author: "GZCTF Mix Demo"
description: |
  Live A&D — patch your team's container, attack the others.
category: "AI"
type: "AttackDefense"
container:
  exposePort: 80
  memoryLimit: 128
  cpuCount: 1
  storageLimit: 256
ad:
  allowEgress: true
  allowSelfReset: true
```

`AI/koth-ai-hill/challenge.yaml` — a King of the Hill challenge (one shared hill), here pinned to a prebuilt registry image:

```yaml
name: "KotH — AI Hill"
category: "AI"
type: "KingOfTheHill"
container:
  containerImage: "gzctf/echo-http:test"
  exposePort: 80
  memoryLimit: 128
  cpuCount: 1
  storageLimit: 256
```

:::tip
`challenge.yaml` and `challenge.yml` are both accepted. The A&D entry omits `containerImage`, so the platform auto-builds `./src/Dockerfile` (see [Challenge Build Pipeline](/guide/authoring/build-pipeline)); the KotH entry points at a prebuilt registry image instead.
:::

### Bind it

The repo is public, so no token is needed:

1. **Admin → Repo Bindings → add**, with `RepoUrl = https://github.com/dimasma0305/gzctf-mix-demo`, an empty ref (default branch), and `IntervalSeconds = 60`.
2. Wait one poll cycle (or hit **Scan now**). The `.gzevent` becomes the *Type×Category Mix Demo* game and all 72 `challenge.yaml` files are imported; `LastScanMessage` shows `games +1`, `challenges +72`, `failures 0`.
3. Push a change to the repo; the next scan picks up the new commit SHA and re-imports just the changed challenges (unchanged scans short-circuit on the SHA).

:::info
For a **private** repo, the only difference is adding a PAT on the binding (and `Contents:write` if you want [`PushOnEdit`](#pushonedit-write-approved-edits-back-to-the-repo)). The layout, manifest, and challenge files are identical.
:::
