# Honeypots

Honeypots are decoy services, routes, and credentials wired throughout the platform that **no legitimate player should ever touch**. They have no role in solving any challenge — they exist purely to catch automated reconnaissance: link-following web scanners, broad infrastructure port-sweeps, and AI agents that chase every URL and open port they discover. When something hits a honeypot, the platform attributes the hit to a team where it can, files a suspicion signal, and lights up the admin live feed.

This page documents the fork-exclusive honeypot subsystem. It feeds directly into the broader cheat-detection pipeline described in [/guide/features/anti-cheat](/guide/features/anti-cheat).

:::info
Honeypots are a **detection** tool, not a defense. They never block, throttle, or ban — they observe and score. Acting on the evidence is an organizer decision made from the suspicion / cheat reports.
:::

## The threat model

A human solving a CTF challenge clicks the links the challenge gives them. An automated tool does not discriminate: it fetches `/.git/config`, probes `/.env`, knocks on port 22 and 6379, and follows every `href` it can parse. The honeypot subsystem is built around that behavioral difference.

There are two surfaces:

| Surface | What it is | Where it lives |
| --- | --- | --- |
| **HTTP bait routes** | Platform-wide decoy URLs (`/.git/config`, `/wp-login.php`, `/actuator/env`, …) that return plausible-looking content | `HoneypotController` |
| **Protocol/port honeypots** | Low-interaction TCP listeners impersonating services like SSH, Redis, MySQL | `HoneypotPortListenerService` |

On top of those, a background **chain detector** correlates repeated hits from the same team into a single high-confidence signal.

Every hit flows through one service — `HoneypotService` (`IHoneypotService`) — which does attribution, files the suspicion event, and broadcasts to organizers.

## HTTP bait routes

`HoneypotController` is mounted at the application root with `[AllowAnonymous]`, and is hidden from the OpenAPI spec (`ApiExplorerSettings(IgnoreApi = true)`) so the bait paths cannot be enumerated by reading the API documentation. Every bait path is a constant in `HoneypotBait`, so a typo cannot silently break a route or a cross-reference.

Any request to one of these paths records an HTTP honeypot hit (category `"http"`) and returns a deliberately plausible response — convincing enough that the scanner files away the "find" without immediately realizing it tripped a tripwire.

### The bait catalogue

| Category | Bait paths |
| --- | --- |
| Source-control leakage | `/.git/config`, `/.git/HEAD`, `/.svn/wc.db` |
| Filesystem detritus | `/.DS_Store` |
| Credentials / config | `/.env`, `/.aws/credentials` |
| CMS / DB admin | `/wp-admin`, `/wp-admin/`, `/wp-login.php`, `/phpmyadmin`, `/phpmyadmin/`, `/phpmyadmin/index.php` |
| Server / framework debug | `/server-status`, `/actuator`, `/actuator/env`, `/actuator/health`, `/_ignition/execute-solution`, `/cgi-bin/luci` |
| Backup files | `/backup.zip`, `/backup.tar.gz`, `/database.sql` |
| Chain-graph endpoints | `/admin-portal/login`, `/admin-portal/dashboard`, `/api/internal/users.json`, `/internal/debug-console`, `/_debug/console`, `/wp-content/uploads/backup-2024-q3.zip`, `/db-export.php`, `/backups/db-dump.sql` |

The responses are not random. They are a **trap web**: each bait body cross-references sibling baits and the configured port honeypots, forming a directed graph. For example, the fake `/.git/config` advertises the `/.env` and `/.aws/credentials` paths; the fake `/.env` advertises the admin portal, the internal user API, and the Spring Actuator env dump; `/admin-portal/login` links to `/api/internal/users.json` and `/admin-portal/dashboard`, which in turn link to `/db-export.php` and `/backups/db-dump.sql`.

```text
/.git/config ──► /.env ──► /admin-portal/login ──► /admin-portal/dashboard
                  │                │                      │
                  │                ▼                      ▼
                  └──► /.aws/credentials   /api/internal/users.json ──► /_debug/console
```

A human glancing at one of these pages stops. A link-following scanner walks the whole graph — and walking the graph is exactly the fingerprint the chain detector escalates.

:::tip
The bait bodies contain only fake, non-functional secrets (e.g. `AKIAEXAMPLEKEYNOTREAL`, `password_hash = $2y$10$placeholderhash…`). They look like a jackpot to a scraper but grant access to nothing.
:::

Responses vary by intent: most baits return `200` with believable content, some return `404` (`/.svn/wc.db`, `/.DS_Store`, the backup archives) the way a real misconfigured server would, and `/wp-admin` issues a `302` redirect to the fake login page — all of which still record the hit.

## The port listener

`HoneypotPortListenerService` is a `BackgroundService` that runs **one TCP listener per configured port**. It is **disabled by default** and starts nothing unless `HoneypotConfig.Enabled` is `true` and at least one valid port entry is present.

On each accepted connection the listener:

1. Optionally writes a configured **banner** (e.g. an SSH version string), with a 3-second write timeout.
2. Reads up to **1024 bytes** of the client's first send (the "probe"), with a 3-second read timeout.
3. Records a TCP honeypot hit (category `"protocol"`, method `"TCP"`) via `HoneypotService.RecordTcpHit`, then closes the connection.

It is **low-interaction by design** — the goal is detection, not protocol emulation. It never completes a real handshake.

### What a probe records

The first bytes the client sends are encoded compactly for storage. Only the first **64 bytes** are kept in encoded form, but the original byte length is preserved:

```text
len=<total-bytes-received> hex=<first-64-bytes-as-hex> ascii=<first-64-bytes-printable-or-dot>
```

Non-printable bytes (outside `0x20`–`0x7e`) become `.` in the ASCII rendering. The `bait` identifier for a port hit is `"<name>:<port>"`, e.g. `ssh:22` or `redis:6379`.

:::warning
Bind only ports you know are free on the host. The listener binds raw TCP, so do **not** co-bind it on the same address/port as the host's real `sshd`, Postgres, etc. Use a player-facing IP via `ListenAddress` if the host is multi-homed.
:::

## Attribution: turning a hit into a team

A honeypot hit is only useful if it can be pinned to a participation. `HoneypotService` resolves attribution in two steps:

1. **Authenticated request** — for HTTP hits, if the request carries an authenticated user, the service finds that user's active participation in a currently-running game (`StartTimeUtc <= now <= EndTimeUtc`).
2. **Recent IP match** — when there is no authenticated principal (always the case for TCP hits, and common for anonymous scanner traffic), the service falls back to the application log. It looks back over a **60-minute** window for the most recent log entry whose `RemoteIP` matches the hit's source IP and maps that to a username, then to an active participation. (The candidate scan is capped at 500 recent log rows.)

If a participation is resolved, a suspicion event is filed and the hit is marked `attributed = true`. If nothing can be matched, the hit is still logged (at `Warning` level) and broadcast to the live feed, but `attributed = false` and no suspicion event is created.

The detail string stored on the suspicion event looks like:

```text
bait=/.git/config category=http method=GET ip=203.0.113.7 ua=curl/8.4.0
```

For a TCP hit it additionally carries the truncated probe:

```text
bait=redis:6379 category=protocol method=TCP ip=203.0.113.7 ua= probe=len=14 hex=2A310D0A24340D0A50494E47 ascii=*1..$4..PING
```

## The chain detector

`HoneypotChainDetectorService` is a `BackgroundService` that periodically aggregates honeypot hits per participation and escalates teams that have tripped **enough distinct baits inside a sliding window**.

On each sweep it:

1. Reads `SuspicionEvent` rows of type `HoneypotHit` or `HoneypotProtocolHit` newer than the window.
2. Groups them by participation and extracts the `bait=` value from each event's details.
3. Counts **distinct** baits per participation (case-insensitive).
4. For any participation whose distinct-bait count meets or exceeds the threshold, files one `HoneypotChain` suspicion event.

The escalation detail records exactly which baits and how many:

```text
baits=/.env,/.git/config,/admin-portal/login count=3 window=30m
```

The rationale: hitting one bait by accident is plausible; methodically walking several cross-referenced ones is the signature of an automated link-following scanner or agent. `HoneypotChain` carries the highest weight in the system (150) and is a **hard signal** — it is always persisted regardless of other evidence.

:::info
The detector enforces sane floors on its tuning knobs: sweep interval is at least 10 seconds, window is at least 1 minute, and threshold is at least 2 — even if you configure something smaller.
:::

## Configuration

Honeypot settings bind from the `HoneypotConfig` section of `appsettings.json` (see [/config/appsettings](/config/appsettings)). The whole subsystem ships **default-disabled** for the port listeners; the chain detector defaults to **enabled** but only acts once HTTP/protocol hits exist.

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `Enabled` | bool | `false` | Master switch for the TCP port listeners. |
| `ListenAddress` | string | `"0.0.0.0"` | Address the listeners bind to. Falls back to all interfaces if unparseable. |
| `Ports` | array | `[]` | Per-service listener definitions (below). |
| `ChainEnabled` | bool | `true` | Enables the chain detector background sweep. |
| `ChainThreshold` | int | `3` | Distinct baits within the window that trigger a `HoneypotChain` signal (floored at 2). |
| `ChainWindowMinutes` | int | `30` | Sliding window for grouping hits per participation (floored at 1). |
| `ChainSweepIntervalSeconds` | int | `60` | How often the detector sweeps the suspicion table (floored at 10). |

Each entry in `Ports` is a `HoneypotPort`:

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `Name` | string | `""` | Service tag used in the bait id (`"<name>:<port>"`). Required — entries with a blank name are skipped. |
| `Port` | int | — | TCP port to bind. Must be `> 0` and `< 65536`. |
| `Banner` | string? | `null` | Bytes sent on connect. Leave null/empty for protocols where the client speaks first (Redis, Postgres, MongoDB, Memcached). |
| `Enabled` | bool | `true` | Per-port toggle so a service can be parked without deleting it. |

A port entry is only started if `Enabled` is true, the port is in range, and the name is non-empty.

### Example configuration

```json
{
  "HoneypotConfig": {
    "Enabled": true,
    "ListenAddress": "0.0.0.0",
    "ChainEnabled": true,
    "ChainThreshold": 3,
    "ChainWindowMinutes": 30,
    "ChainSweepIntervalSeconds": 60,
    "Ports": [
      {
        "Name": "ssh",
        "Port": 22,
        "Banner": "SSH-2.0-OpenSSH_8.9p1 Ubuntu-3ubuntu0.6\r\n",
        "Enabled": true
      },
      {
        "Name": "redis",
        "Port": 6379,
        "Enabled": true
      },
      {
        "Name": "mysql",
        "Port": 3306,
        "Enabled": false
      }
    ]
  }
}
```

The HTTP bait routes need **no configuration** — `HoneypotController` is always mounted. Only the TCP listeners and the chain detector are config-gated.

### Verifying it works

With the config above applied, you can trip a bait and watch it land in the logs:

```bash
# HTTP bait — returns a plausible fake .env, records a HoneypotHit
curl -s http://your-platform/.env

# Protocol bait — the SSH listener sends its banner, then records a HoneypotProtocolHit
nc your-platform 22
```

The application log emits a `Warning` for each hit:

```text
warn: GZCTF.Services.HoneypotService[0]
      Honeypot hit: bait=/.env category=http method=GET ip=203.0.113.7
      ua=curl/8.4.0 user=alice team=TeamRed probeLen=0 attributed=True
```

## How hits surface to organizers

### Live admin feed

Every hit — attributed or not — is broadcast over the admin SignalR hub (`AdminHub`) via the `ReceivedHoneypotHit` client method, carrying a `HoneypotHitModel`:

```json
{
  "time": "2026-05-29T12:34:56.789Z",
  "bait": "/.git/config",
  "category": "http",
  "method": "GET",
  "ip": "203.0.113.7",
  "ua": "python-requests/2.31.0",
  "user": "alice",
  "team": "TeamRed",
  "attributed": true
}
```

`user` and `team` are populated only when attribution succeeded; `attributed` reflects whether a suspicion event was created.

### Suspicion / cheat reports

When a hit is attributed, `HoneypotService` calls `ISuspicionService.AddSuspicion`, which writes a row to the `SuspicionEvents` table and adds the rule's weight to the participation's running `SuspicionScore`. Honeypot signals occupy the high-confidence end of the suspicion taxonomy:

| Signal | Weight | Tier | Meaning |
| --- | --- | --- | --- |
| `HoneypotHit` | 70 | Strong | Hit a platform honeypot HTTP route — automated reconnaissance. |
| `HoneypotProtocolHit` | 90 | Hard | Connected to a honeypot protocol service (SSH, Redis, …) — broad infra scan. |
| `HoneypotChain` | 150 | Hard | Followed multiple cross-referenced baits — automated link-following scanner or agent. |

`HoneypotProtocolHit` and `HoneypotChain` are **hard signals** — always persisted regardless of corroboration. `HoneypotHit` is a **strong signal**. These weights and descriptions are the built-in defaults; if you have edited the corresponding rule in the suspicion-rule configuration, your custom weight is used instead.

:::tip
The chain signal is intentionally far heavier than a single hit. One `/.env` probe (70) is suggestive; three distinct baits in 30 minutes adds `HoneypotChain` (150) on top of the individual hits, pushing the team well clear of any soft-signal threshold.
:::

Because honeypot evidence flows through the same `SuspicionEvents` pipeline as every other detector, it shows up alongside IP-correlation, fast-solve, and flag-egress signals in the cheat report. See [/guide/features/anti-cheat](/guide/features/anti-cheat) for how scores aggregate, how hard vs. strong vs. soft tiers gate one another, and how to review and act on flagged participations. Related deception and exfil detection lives in the attack/defense tooling described at [/guide/features/attack-defense](/guide/features/attack-defense).

## Operational notes

- **No legitimate path leads here.** None of the bait routes or ports are referenced by real challenges, the scoreboard, or the player UI, so an honest player using the platform normally will never trip one.
- **Attribution is best-effort.** TCP hits and anonymous HTTP hits rely on the 60-minute IP-to-user log fallback. A scanner running from an IP the platform has never associated with a logged-in user is logged and broadcast but not scored.
- **De-duplication.** The suspicion layer skips a new event if an identical `(participation, type, details)` row already exists, so repeatedly fetching the same bait with the same metadata will not inflate the score; only **distinct** baits drive the chain detector.
- **Tune for your audience.** If your event legitimately involves recon-heavy challenges on separate infrastructure, raise `ChainThreshold` or narrow `ChainWindowMinutes` so normal play does not brush the trap web.
