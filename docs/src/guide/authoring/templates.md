# Challenge Templates

The platform repo, [`TCP1P/gzctf-platform-template`](https://github.com/TCP1P/gzctf-platform-template), ships four ready-to-edit challenge scaffolds under `challenges/`. Each is a complete, runnable example — copy a folder, edit it, and you have a valid challenge. This page explains what each scaffold is for, the shared folder layout, and how to get a challenge onto the platform.

:::tip
You don't have to start from scratch. Every scaffold already has a working `challenge.yml`, a buildable `src/`, and a real `solver/`. The fastest path is "copy the closest folder, then edit," not "write a `challenge.yml` from memory."
:::

:::tip Want a fuller worked example?
The scaffolds here are deliberately minimal — one of each type, with a toy bug. For a complete, realistic A&D/KotH event you can import as-is, point a repo binding at [`TCP1P/TCP1PADTesting`](https://github.com/TCP1P/TCP1PADTesting). It's a focused four-challenge game — two Attack & Defense services (an OWASP web portal and a heap-corruption pwn binary) and two King of the Hill hills (an OWASP web hill and a binary hill) — wired with real vulnerabilities, auto-built `./checker` images, and reference solvers. See [/guide/authoring/repo-bindings](/guide/authoring/repo-bindings) for how to import it.
:::

## The four scaffolds

The repo's `challenges/` directory holds one folder per challenge type. Pick by what your challenge needs at runtime.

| Folder | `type:` | What it is | Use it when |
|---|---|---|---|
| `static-attachment/` | `StaticAttachment` | Files-only. Players download everything in `dist/`; the flag is matched server-side against the `flags:` list. Same static flag for every team, no server. | Crypto, reverse, forensics, misc — anything that doesn't need a live server. |
| `dynamic-container/` | `DynamicContainer` | One container per team, each with a unique flag injected via the `GZCTF_FLAG` env var (built from `flagTemplate`). The flag is fixed for the life of the container — it does not rotate. | Pwn, web — anything that needs a per-team live instance, where a leaked flag should identify who leaked it. |
| `attack-defense/` | `AttackDefense` | A persistent per-team service plus a checker. The platform plants a fresh flag into `/flag` every tick; defenders patch the bug, attackers steal flags from other teams. | Attack & Defense rounds — a vulnerable service teams both run and exploit. |
| `king-of-the-hill/` | `KingOfTheHill` | One **shared** hill container for the whole game. There is no flag — teams plant their game-wide control token in the marker file `/koth/king` to hold it, health-checked each tick. | A single contested objective every team fights to control simultaneously. |

:::info
A&D and KotH both run on the same A&D engine, so they reuse the `container:` block and the `ad:` block — but A&D gives **one container per team** while KotH runs **one shared hill** for everyone. See [/guide/features/attack-defense](/guide/features/attack-defense) and [/guide/features/king-of-the-hill](/guide/features/king-of-the-hill) for the full mechanics.
:::

## The common folder layout

Every scaffold follows the same shape:

```text
<challenge>/
├── challenge.yml   # the manifest: type, description, flags/container/ad blocks
├── src/            # the challenge itself; auto-built from ./src/Dockerfile for container types
├── solver/         # a working solver so reviewers can verify the challenge
│   └── solve.py
└── dist/           # files players download (the binary, ciphertext, pcap, source…)
```

- **`challenge.yml`** — the manifest. Its schema is validated against the `$schema` URL pinned at the top of each file. Full field reference: [/guide/authoring/challenge-yaml](/guide/authoring/challenge-yaml).
- **`src/`** — the challenge source. For the three container types (`DynamicContainer`, `AttackDefense`, `KingOfTheHill`), the platform **auto-builds `./src/Dockerfile`** when `containerImage` is omitted. `StaticAttachment` has no build — its `src/` just holds a reference copy of the flag, which is **not** shipped to players.
- **`solver/`** — a working solver (typically `solve.py`) so reviewers can verify the challenge actually solves. For A&D this is your attack exploit; for KotH it fetches your round token and plants it in the hill.
- **`dist/`** — exactly the files players download. The platform packages whatever is in here. For `StaticAttachment` this is required (it's the whole challenge); for container types it's optional (hand out source/binary if you want).

:::warning
`value:` and any `visible:`/`enabled:` fields in `challenge.yml` are **ignored on import**. Points and visibility are admin-controlled after review, so the `value: 1000` you see in the scaffolds is a placeholder — leave it. See [/guide/features/scoring](/guide/features/scoring) for how points are actually set.
:::

## How to start

1. **Copy the closest scaffold folder** and rename it.
2. **Edit `challenge.yml`** — set `name`, `author`, `description`, and the type-specific block (`flags:` / `container:` / `ad:`).
3. **Fill in `src/`** (your service or attachment) and **`dist/`** (player downloads), and write a real `solver/`.
4. **Import it** — three ways, in order of scale:

### A — Repo binding (recommended)

Commit your challenges to a Git repo, then point the platform at it: **admin → Repo Bindings → Add**, with the repo URL, an empty ref (default branch), and `IntervalSeconds` (default `60`, clamped to `[60, 86400]`). For a public repo leave the token empty. The platform clones the repo itself, globs every `.gzevent` recursively, and imports the `challenge.yml` files under each event — container types auto-build their `./src/Dockerfile`. After the first poll (or hit **Scan now**) the import re-runs automatically on every push, so this is the path that scales to a whole event.

This is exactly how you'd import [`TCP1P/TCP1PADTesting`](https://github.com/TCP1P/TCP1PADTesting). Full walkthrough — binding fields, scan cadence, `Status`/`TokenStatus`, private-repo tokens — is in [/guide/authoring/repo-bindings](/guide/authoring/repo-bindings).

### B — Web upload (one-off, gated)

Zip the challenge folder and drop it at `https://PUBLIC_ENTRY/games/<id>/submit`. This requires an admin to enable **Allow user submissions** for that game (admin → game → Info); otherwise the page is disabled and the API returns `403`. Submitted challenges land in `Pending` review.

The first three types (`static-attachment`, `dynamic-container`, `attack-defense`) are also **one-click downloads on the in-app submit page**. King of the Hill has **no download button** — zip the folder and upload it.

### C — Admin tarball import (one-off)

An admin can import a packaged event/challenge archive directly through the admin import API without a repo binding — useful for a one-shot load of an existing tree. See [/guide/authoring/repo-bindings](/guide/authoring/repo-bindings) for the import endpoints and when to use each.

:::info
Repo bindings (A) are the supported, server-side way to keep a game in sync — prefer them for anything beyond a one-off challenge.
:::

:::tip Stopping a challenge from re-syncing
Deleting a challenge in the admin UI removes it from the platform but **not** from the repo, so a repo binding will re-import (resurrect) it on the next scan. To keep it gone, add `ignore: true` to its `challenge.yml`:

```yaml
name: "old-challenge"
type: "StaticAttachment"
ignore: true   # importer skips this challenge entirely — never created/updated
```

`ignore: true` makes the importer skip the challenge (no create, no update). It does **not** delete an already-imported copy — remove that once in the UI. Drop the key to start syncing again.
:::

## Type-specific details

### static-attachment

No container, no build. Put player files in `dist/`; list accepted flags under `flags:`. Every team submits the **same** flag.

```yaml
name: "static-attachment"
author: "your-name"
type: "StaticAttachment"   # don't touch this value
value: 1000                 # placeholder — points are admin-controlled

flags:
  - "flag{replace-me}"

provide: "./dist"           # directory handed to players for download
```

The scaffold keeps a reference copy of the flag at `src/flag.txt` — that file is **not** shipped to players, it's just so you (and reviewers) know the answer.

### dynamic-container

One instance per team with a unique flag. The platform substitutes `[TEAM_HASH]` in `flagTemplate` per team and injects the result as the `GZCTF_FLAG` env var at container start. The flag is fixed for the life of the container (it does **not** rotate — that's A&D).

```yaml
type: "DynamicContainer"    # don't touch this value
value: 1000

container:
  # containerImage omitted -> platform auto-builds ./src/Dockerfile
  flagTemplate: "flag{ctf_[TEAM_HASH]_dynamic}"
  exposePort: 8011
  memoryLimit: 256
  cpuCount: 1
  storageLimit: 256

provide: "./dist"           # optional: hand players the source/binary
```

The scaffold's `src/challenge.py` is a pure-stdlib `socketserver` TCP service that reads `GZCTF_FLAG` — no `socat` / shell wrapper.

### attack-defense

A full A&D challenge. The `container:` block describes the per-team **service**; the `ad:` block carries this service's own properties.

```yaml
type: "AttackDefense"       # don't touch this value
value: 1000

# Per-team SERVICE image + port (containerImage omitted -> auto-build ./src/Dockerfile)
container:
  exposePort: 80
  memoryLimit: 256
  cpuCount: 1
  storageLimit: 256

ad:
  # checkerImage: "ghcr.io/your-org/attack-defense-checker:latest"
  allowEgress: true         # default true; false = no outbound at all
  allowSelfReset: true      # default true; can teams self-reset this container?
  # Checker timing (getflagWindowFraction default 0.5, minGracePeriodSeconds
  # default 3) is EVENT-WIDE — set it in the .gzevent manifest, not here.
```

Key points specific to A&D:

- **`/flag`** — you do **not** author flags for A&D. The platform plants a fresh per-team flag into `/flag` inside every team's container at the start of each tick. There is **no `GZCTF_FLAG` env var** for A&D (an env baked at container start would go stale after the first rotation); read the live flag from `/flag` (its path is also in `GZCTF_FLAG_FILE`). Your service must surface that flag through the intended bug.
- **The `checker/` harness** — build `./checker` and push it, then set `ad.checkerImage`. Add your tests in `checker/checks.py`: write a function taking a `Target`, decorate it with `@check`, return normally to pass, or `raise Mumble("why")` if the service is up but wrong. `t.get(path)` / `t.post(path)` raise `Offline` for you when unreachable. Don't edit `checker.py` or `run.py`. The harness speaks the enochecker3 exit-code contract (`0 Ok / 1 Mumble / 2 Offline / 3 InternalError`) and reports the worst verdict each tick.
- **`allowEgress: true`** (default) lets team containers reach the public internet — most A&D services expect outbound access. Private and link-local ranges are blocked regardless. Set `false` to sandbox a service with no egress.

:::warning
`ad.checkerImage` must be a **pushed registry reference** — local `./checker` paths are not auto-built for checkers, and local-only tags can be reaped by image-prune jobs. Omit `checkerImage` entirely to fall back to a plain TCP-reachability probe: services that respond on their port score SLA `Ok`, silent ones score `Offline`, with no flag-correctness (`Mumble`) distinction.
:::

:::info
Tick length, flag lifetime, warmup, reset cooldown, and snapshot-download are **event-wide** settings — they live in the `ad:` block of the `.gzevent` manifest (and admin → game → Info), not in `challenge.yml`. A round spans the whole game, so every A&D service shares one tick. The per-challenge `ad:` block only carries `checkerImage`, `allowEgress`, and `allowSelfReset`. The checker-timing knobs (`getflagWindowFraction`, `minGracePeriodSeconds`) are event-wide too — set them in the `.gzevent` manifest's `ad:` block, not per challenge.
:::

### king-of-the-hill

One **shared** hill for the whole game. KotH reuses the `container:` block, but there is one container total (not one per team).

```yaml
type: "KingOfTheHill"       # don't touch this value
value: 1000

# ONE shared hill (containerImage omitted -> auto-build ./src/Dockerfile)
container:
  exposePort: 80
  memoryLimit: 256
  cpuCount: 1
  storageLimit: 256

ad:
  # checkerImage: "ghcr.io/your-org/king-of-the-hill-checker:latest"
  allowEgress: false        # the hill is a target; it rarely calls out itself
  allowSelfReset: false     # MUST stay false — see below
```

Key points specific to KotH:

- **`/koth/king`** — there is no flag. The platform issues every team one game-wide control token (`GET /api/Game/{id}/Ad/Koth/Token` — no challenge id needed). A team's exploit must land that token in the marker file **`/koth/king`** on the hill. Each tick the platform reads `/koth/king` from the container itself, matches it to the team it was issued to, and — if the checker also reports `Ok` — credits that team hold points. Holding a *broken* hill (checker not `Ok`) costs a penalty instead. The token rotates only when the hill resets (every `KothRefreshTicks` rounds), so teams re-plant after each reset, not every tick. Keep the marker at **exactly** `/koth/king`.
- **Replace the toy write** — the scaffold's `src/service.py` ships an intentionally trivial open `POST /king` so it runs out of the box. The whole challenge is replacing that with a real vulnerability — making the write to `/koth/king` something teams must *earn*.
- **The `checker/` harness is health-only** — same harness and push rules as A&D, but KotH runs it with **no flag**, purely to decide `Ok` / `Mumble` / `Offline` (which gates hold points vs penalty). Put flag-free health assertions in `checker/checks.py` — do **not** reference `t.flag`. Omit `checkerImage` for a TCP-reachability probe.

:::danger `allowSelfReset` must be false
The hill is **shared**, so letting one team self-reset it would wipe every team's foothold and the current king. KotH wipes are governed by the game-level refresh setting, not team self-reset. Keep `allowSelfReset: false`.
:::

:::info
KotH scoring knobs — hold points per tick (default `1.0`) and the N-tick refresh that wipes the hill (default `5`) — are game-level but currently **fixed at their defaults**: they are neither carried in the `.gzevent` manifest nor exposed in the admin UI. Tuning them today means editing the `Game` row directly (`Game.KothHoldPointsPerTick` / `Game.KothRefreshTicks`). The shared `ad:` tick block (tick length, warmup) from `.gzevent` *does* apply to KotH.
:::

## Where to go next

- [/guide/authoring/challenge-yaml](/guide/authoring/challenge-yaml) — the full `challenge.yml` schema and every field.
- [/guide/authoring/repo-bindings](/guide/authoring/repo-bindings) — connecting a repo so the platform imports and re-syncs challenges (and the TCP1PADTesting walkthrough).
- [/guide/features/attack-defense](/guide/features/attack-defense) — how A&D ticks, flags, checkers, and scoring work end to end.
- [/guide/features/king-of-the-hill](/guide/features/king-of-the-hill) — how KotH tokens, the shared hill, and hold scoring work.
