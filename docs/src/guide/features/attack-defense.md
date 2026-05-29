# Attack & Defense

This fork extends the upstream GZ::CTF jeopardy platform with a full **Attack & Defense (A&D)** engine: every team runs the same vulnerable service, the platform plants a fresh per-team flag into each box every tick, and teams score by stealing each other's flags while keeping their own service patched *and* passing.

A&D challenges are a distinct `ChallengeType` (`AttackDefense`) alongside the related **King of the Hill** (`KingOfTheHill`) type. The two share the round engine, the WireGuard VPN, the checker plumbing, and the scoreboard infrastructure, but differ in container topology and scoring. This page documents A&D; KotH is noted where the engine diverges.

:::info
A&D challenges are authored with the same challenge YAML as jeopardy, plus an `ad:` block. See [/guide/authoring/templates](/guide/authoring/templates) for the attack-defense template and [/guide/authoring/challenge-yaml](/guide/authoring/challenge-yaml) for the full schema. Scoring math (attack / defense / SLA) is on [/guide/features/scoring](/guide/features/scoring).
:::

## The round / tick model

An A&D game is divided into **rounds** (a "round" and a "tick" are the same thing in this engine — one `AdRound` row per tick). Each round defines a time window and the set of flags that are live during it.

### Warmup

When a game goes live (`StartTimeUtc ≤ now ≤ EndTimeUtc`) the engine does **not** immediately start round 1. It waits out a warmup window so teams can connect, download their VPN config, and find their boxes before flags start rotating and the SLA clock starts.

- Warmup length is `Game.AdWarmupSeconds`, **default `1800`** (30 minutes).
- During warmup there is no `AdRound`, so there are no flags, no SLA checks, and the `/Targets` endpoint returns an empty list.
- `AdRoundScheduler` bootstraps **round 1** on the first poll after `StartTimeUtc + AdWarmupSeconds`.

### Tick length and advance

- Tick length is `Game.AdTickSeconds`, **default `60`**. A round spans `StartedAt … StartedAt + AdTickSeconds`; the whole game shares one tick window (it is an event-wide knob, not per-challenge).
- `AdRoundScheduler` polls every **5 s**. When the latest round's `EndsAt` is in the past, it advances to the next round.
- Rounds can also be force-advanced from the admin UI. Both the auto-advancer and the manual button call the **same** `AdRoundService.AdvanceAsync` code path, so manual and automatic advances can never diverge on scoring semantics or flag format.
- Scoring can be paused (`Game.AdScoringPaused`): while paused, the scheduler stops advancing rounds, the checker stops accruing SLA, and flag submissions are frozen (returned with status `paused`).

### What `AdvanceAsync` does on each advance

1. Computes `nextNumber = (previous round number) + 1` and the new `[StartedAt, EndsAt]` window.
2. In a single DB transaction: inserts the new `AdRound`, then mints one fresh flag per `(team, A&D challenge)` (`AdFlag` rows) and one rotating control token per `(team, KotH challenge)` (`KothToken` rows). The transaction makes the round, its flags, and its tokens become visible atomically — a checker tick can never see a live round whose flags/tokens haven't been written yet.
3. **After** the commit (outside the transaction, best-effort), plants each flag into the team's container. A slow or dead container can't block the round from going live; a failed plant only costs that team one tick of attackability and is retried next round.

:::tip
Round advance is deliberately sub-second. The SLA checker runs as a **separate** background service (`AdCheckerService`, 10 s cadence) so a slow check of N teams × M challenges never sits on the round-advance critical path.
:::

## Per-team containers and bridges

A&D spins up **one container per `(team, challenge)`** — every accepted team gets its own private instance of every enabled A&D challenge.

`AdContainerManager` reconciles desired state every **15 s**:

- For each active A&D game, every accepted `Participation` must have a live container for every enabled A&D challenge. Missing or dead ones are (re)launched.
- Late join works implicitly: a team accepted mid-game gets its containers on the next reconcile tick (≤ 15 s), or immediately via a one-shot ensure when the participation flips to Accepted.
- A cheap lock-free pre-check (bulk DB state + a live "running containers" set) gates an authoritative per-container `docker inspect` / pod read, taken under a per-`(team, challenge)` lock so the reconciler, accept-time ensure, and self-reset can't race into double-launches or orphans.
- At game end, each container is snapshotted (Docker only — see below) and destroyed.

### Networking

Each container lands on one of two provider networks depending on the challenge's `ad.allowEgress` flag (`GameChallenge.AdAllowEgress`, **default `true`**):

| `ad.allowEgress` | Network mode | Effect |
| --- | --- | --- |
| `true` (default) | `Open` bridge | Box can reach the internet (hardened to deny private + link-local ranges, e.g. cloud metadata). Teams reach each other over the VPN. |
| `false` | `Isolated` bridge | Deny-all egress; reachable only over the A&D VPN. |

Toggling `ad.allowEgress` mid-game is detected as **network drift**. A live container is moved between the Open and Isolated bridges *in place* (connect-new-then-disconnect-old) so the team keeps its patches; the engine only recreates the container if the live move fails.

Resource limits come from the challenge: `CPUCount` (default `1`), `MemoryLimit` (default `64` MiB), `StorageLimit` (default `256` MiB). The exposed service port is `ExposePort` (default `80`).

:::warning
King of the Hill is the exception to "one container per team": a KotH challenge has **one shared hill container** for the whole game. Every team attacks the same box and plants its rotating token into the `/koth/king` marker. The hill resets to base image every `Game.KothRefreshTicks` ticks (default `5`), wiping footholds and the marker.
:::

## The rotating flag

Every tick, `AdvanceAsync` generates a fresh per-team flag of the form `flag{<24 random url-safe-base64 bytes>}` and plants it into that team's container. The flag is **not** delivered as an environment variable — it is written to a file, and challenge code is told where to read it via **`GZCTF_FLAG_FILE`**.

| | Docker | Kubernetes |
| --- | --- | --- |
| Flag file path | `/flag` | `/gzctf-flag/flag` |
| `GZCTF_FLAG_FILE` env | `/flag` | `/gzctf-flag/flag` |
| Delivery | Read-only host-backed **bind mount**; the platform rewrites the host file each tick and the `:ro` mount picks it up instantly | **Pull** model: a flag-writer sidecar polls a per-service URL and writes the file to a read-only volume (virtual nodes can't `exec`) |

```bash
# Inside the service container — read the live flag from the path the platform gives you.
cat "$GZCTF_FLAG_FILE"      # Docker: /flag, K8s: /gzctf-flag/flag
# flag{Vu1n3rabl3Serv1ce_...}
```

:::danger
There is **intentionally no `GZCTF_FLAG` environment variable** for an A&D service. An env var is baked at container creation and frozen for the container's life — it would go stale after the very first rotation and mislead challenge code. Always read the **file** at `GZCTF_FLAG_FILE`. (The `GZCTF_FLAG` env *is* passed to the **checker** as the flag it should retrieve — that is a different contract; see below.)
:::

### Why the flag survives RCE (Docker)

The Docker bind mount is read-only **and host-backed**, and the flag is written as `root`-owned `644`:

- A service that runs as a **non-root** user can `read` the flag (the intended exploit target) but cannot delete it — removing `/flag` needs write on `/`, which stays root-only — nor overwrite the root-owned file.
- Even if the service runs as root and a full RCE wipes `/flag`, the next tick re-plants it; the checker's getflag step scores the gap as SLA loss for that one tick.

The legacy fallback (a `docker exec` write into `/flag`) still exists for containers launched before the bind-mount feature; it runs as `User="0"` regardless of the image's `USER`.

### Flag lifetime

A captured flag stays submittable for `Game.AdFlagLifetimeTicks` rounds (**default `5`**). A submission older than `currentRound − lifetime + 1` is rejected as `expired`. This is what makes defense matter: patch quickly and an attacker only has a few ticks to cash in a stolen flag.

## The checker (SLA)

Every tick, `AdCheckerService` runs a health/functionality check against each team's service. The verdict drives the **SLA** term of the score: a team that is breached but keeps its service *green* still earns its defense/SLA points; a team whose box is down loses them.

### enochecker3 exit-code contract

A custom checker image is run **once per `(team, challenge)`** on the target's network, with an [enochecker3](https://github.com/enowars/enochecker3)-style environment contract, and its **process exit code** is mapped to a status:

| Exit code | Status | Meaning |
| --- | --- | --- |
| `0` | `Ok` | Service up and functioning correctly (flag retrievable, expected behavior) |
| `1` | `Mumble` | Service reachable but misbehaving / wrong responses |
| `2` | `Offline` | Service unreachable |
| `3` (any other) | `InternalError` | Checker itself failed — **not the team's fault**, scored with no SLA penalty |

The checker receives these environment variables:

```text
GZCTF_ACTION=check
GZCTF_TARGET_IP=<team container IP>
GZCTF_TARGET_PORT=<challenge ExposePort, default 80>
GZCTF_FLAG=<the flag the platform planted this round — what the checker must retrieve>
GZCTF_ROUND=<round number>
GZCTF_TEAM_ID=<participation id>
GZCTF_CHALLENGE_ID=<challenge id>
```

Checker runs are bounded (256 MiB / 0.5 CPU, timeout `Ad:Checker:TimeoutSeconds`, default `30 s`, capped at `600`). Checks are dispatched with bounded parallelism (`Ad:Checker:MaxParallel`, default `10`). Each check is retried up to **3 times** (1.5 s apart), returning on the first `Ok`, so a single dropped packet doesn't cost a team a full tick of SLA.

:::tip
Checks fire at a **jittered, per-(service, round) randomized offset** inside the tick — never before `Game.AdMinGracePeriodSeconds` (default `3`, so the box settles after the flag plant) and within `Game.AdGetflagWindowFraction` of the tick after that (default `0.5`). The jitter is re-rolled each round, so teams can't predict the check window and only "un-hide" their box during it. Only the **latest** round is ever checked.
:::

### `ad.checkerImage` must be a PUSHED registry image

The checker image is launched on demand on the worker. If it isn't already present locally it is pulled by reference:

```yaml
ad:
  checkerImage: registry.example.com/team/my-service-checker:latest   # MUST be pushed & pullable
```

:::warning
`ad.checkerImage` must be a **pushed, pullable registry image**. A locally-built-but-never-pushed tag works only on the exact host that built it; the moment it's pruned or the check runs elsewhere, the pull fails and every check lands as `InternalError` (no SLA, but also no functional verification). If you build a checker locally, push it before the game.
:::

### Falling back to a TCP probe

If you **omit** `ad.checkerImage`, the engine falls back to a built-in **TCP-reachability probe** against the target's exposed port:

- Docker: gzctf is attached to the challenge bridges and TCP-connects directly (no per-check container).
- Mapping: connect succeeds → `Ok`, otherwise → `Offline`. **No `Mumble` distinction is possible** — a TCP probe can't tell "wrong response" from "correct response."

This is fine for "is the port open" SLA, but for real functionality checking (does the service still serve flags correctly after a patch?) you want a proper enochecker3 image.

```text
no ad.checkerImage  →  TCP probe   →  Ok (port open) | Offline (port closed)
ad.checkerImage set →  your image  →  Ok | Mumble | Offline | InternalError
```

SLA credit is computed once at persist time from the *previous* verdict (the "Ok right after a down tick" recovering case earns partial credit) and frozen with the round's field-size factor so a later accept/reject can't retroactively rescale historical SLA. See [/guide/features/scoring](/guide/features/scoring) for the exact formula.

## VPN access (WireGuard)

Teams reach each other's boxes over a per-user WireGuard tunnel. Each player downloads their own `.conf`:

```
GET /api/Game/{id}/Ad/Vpn/Config        (session auth)
```

On first call this generates a fresh X25519 keypair and allocates a `/32` from the client CIDR; later calls return the same peer's config. The returned file looks like:

```text
[Interface]
PrivateKey = <generated>
Address = 10.13.37.42/32
DNS = 1.1.1.1

[Peer]
PublicKey = <server pubkey from the sidecar>
Endpoint = <Ad__Vpn__ServerEndpoint>
AllowedIPs = 10.13.37.0/24, <auto-discovered challenge subnets>
PersistentKeepalive = 25
```

`AllowedIPs` is resolved in order: an explicit `Ad__Vpn__AllowedIps` override → live-discovered challenge subnets (the zero-config path) → just the VPN subnet (degraded; clients can only reach each other, not challenge boxes).

### Server config

`AdWireGuardSyncService` renders the server's `wg0.conf` from the `AdVpnPeer` table every **15 s** and writes it to the shared `/wg-config` volume; the WireGuard sidecar's inotify loop runs `wg syncconf` on change (no restart). Knobs (env vars prefixed `Ad__Vpn__`):

| Env var | Default | Meaning |
| --- | --- | --- |
| `Ad__Vpn__ConfigDir` | `/wg-config` | Shared-volume mount |
| `Ad__Vpn__ServerEndpoint` | `127.0.0.1:51820` | UDP endpoint clients dial (**must override** for non-host access) |
| `Ad__Vpn__ClientCidr` | `10.13.37.0/24` | Subnet for peer `/32`s (server lives at `.1`) |
| `Ad__Vpn__ListenPort` | `51820` | Server-side WG port |
| `Ad__Vpn__Dns` | `1.1.1.1` | DNS pushed to clients |

See [/config/appsettings](/config/appsettings) for where these live in configuration.

### Revoke-on-kick

The sync service only renders a peer while **both** conditions hold: the participation is still `Accepted` **and** the user is still on the team roster. So kicking, suspending, or having a member leave automatically revokes their tunnel within one tick (≤ 15 s) — the kicked member's `/32` simply stops appearing in `wg0.conf`. The same "still a member" guarantee gates the API token and the SSH jump host.

:::info
A&D also exposes an SSH jump host (`/Ssh/Key` — upload or server-generate a key) so teams can shell into their own box to patch it. SSH keys are revoked on the same membership gate.
:::

## Attack flow

1. **Find targets.** Poll `GET /api/Game/{id}/Ad/Targets` (dual auth: session cookie **or** `Authorization: Bearer ad_...`). Returns every *other* team's container IP + port per enabled challenge, plus the last health verdict, so exploit scripts know where to aim. Empty until warmup elapses.
2. **Exploit over the VPN** and read the victim's `flag{...}` from their running service.
3. **Submit it.** POST the captured flag(s) to the submit endpoint using your team **API token** (the Toolkit token):

```
POST /api/Game/{id}/Ad/Submit
Authorization: Bearer ad_xxxxxxxxxxxxxxxx
Content-Type: application/json

{ "flags": ["flag{stolen_from_team_b}", "flag{stolen_from_team_c}"] }
```

```json
{
  "acceptedCount": 1,
  "totalPoints": 10.0,
  "results": [
    { "flag": "flag{stolen_from_team_b}", "status": "accepted", "points": 10.0, "flagPlantedAtRound": 12 },
    { "flag": "flag{stolen_from_team_c}", "status": "expired", "flagPlantedAtRound": 4 }
  ]
}
```

Per-flag result statuses: `accepted`, `wrong` (unrecognized / empty), `self_attack` (your own flag), `expired` (older than `AdFlagLifetimeTicks`), `duplicate` (you already submitted it), `not_started`, `ended`, `paused`. Submissions are only accepted inside the game window and while scoring isn't paused. First-blood weighting on a given flag is decided by a per-flag advisory lock so two teams submitting the same stolen flag concurrently get a consistent capture order. See [/guide/features/scoring](/guide/features/scoring) for attack point values.

### The team API token (Toolkit)

Each team member manages their own token under `/api/Game/{id}/Ad/Token`:

| Method | Action |
| --- | --- |
| `POST` | Generate / rotate — returns the plaintext **once** |
| `GET` | Read the hint (never the plaintext) |
| `DELETE` | Revoke |

The token authorizes `Submit`, `Targets`, and the KotH `Token`/`State` endpoints for scripted play. It is bound to the user's membership: if the member is kicked, the token stops working immediately (the roster lookup fails), the same revoke-on-kick guarantee as the VPN.

## Defense flow

Defense is the inverse of attack: keep your own box from leaking flags **without** breaking it.

- **Patch the vulnerability** (shell in over the SSH jump host or work through the VPN) so other teams can no longer read your `/flag`.
- **Keep the checker green.** A patch that breaks functionality turns your SLA verdict to `Mumble`/`Offline` and you lose the defense/SLA term — over-patching is penalized just like being breached.
- **Self-reset** if you wedge your box: `POST /api/Game/{id}/Ad/Services/{adTeamServiceId}/Reset` rebuilds the container from the base image (new IP). Enabled per-challenge via `ad.allowSelfReset`; rate-limited by `Game.AdResetCooldownMinutes` (default `5`), only inside the game window.

:::tip
The "what did the team change" diff (admin view + post-game) filters out runtime churn — the flag mount, `/tmp`, `/run`, package caches, Python `__pycache__`, and ancestor directories — so it surfaces deliberate patches, not noise. An attacker foothold dropped into one of those filtered paths won't show in the diff; use the live shell / raw file inspection for that.
:::

### Post-game snapshot (Docker only)

Snapshotting is **on by default**: at game end the engine commits each team's container to a gzipped image tarball and stores it, unless the operator disables it. This is an event-wide policy (`ad.allowSnapshotDownload`, default `true`) set in the `.gzevent` manifest's `ad:` block (or admin game settings) — **not** a per-challenge key. Teams can download their own box's final state from `/Services/{adTeamServiceId}/Snapshot` **only after the game ends**. Snapshotting is a Docker-only feature for v1; on Kubernetes only a filesystem-change list is captured.

## Provider notes

The engine runs on both Docker and Kubernetes via the container abstraction, with some Docker-only features:

| Capability | Docker | Kubernetes |
| --- | --- | --- |
| Per-team containers / bridges | Yes | Yes (pods + NetworkPolicy) |
| Flag delivery | `/flag` read-only bind mount, rewritten each tick | `/gzctf-flag/flag` written by a pull sidecar polling `PodFlag` (`Ad:FlagPullBaseUrl` must be set, and be an IP) |
| Custom + TCP checker | Yes | Yes |
| End-of-game image snapshot | Yes | No (change-list only) |
| KotH leader cooldown | Yes (iptables in the WG sidecar) | No (front-runner can immediately re-pwn the fresh hill) |

:::info
On Kubernetes, the flag is pulled rather than pushed: the sidecar polls `GET /api/Game/{id}/Ad/PodFlag/{participationId}/{challengeId}/{token}`, authenticated by an unguessable HMAC of `(participation, challenge)` that only ever yields the owner's own flag. If `Ad:FlagPullBaseUrl` is unset, flags can't reach team pods.
:::
```
