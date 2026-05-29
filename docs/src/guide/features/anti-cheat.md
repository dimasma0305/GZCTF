# Anti-Cheat & Traffic Analysis

This fork extends GZ::CTF with an integrated anti-cheat layer that upstream does not have. It aggregates dozens of behavioral and network signals into a single **suspicion score** per team participation, surfaces them through a dedicated **cheat report** endpoint, and adds a packet-level **flag-egress tracer** plus a flow inspector for the traffic the platform already proxies.

This page documents the four signal families you asked about — cross-team IP correlation, stolen-flag detection, container-access-based submission correlation, and pcap/flow flag-egress inspection — then explains how everything is aggregated and how you act on it.

:::info
All signals feed one common pipeline: each detection produces a `SuspicionEvent` (a typed row with a weight and a free-text `Details` payload) attached to a team's `Participation`, and the participation's `SuspicionScore` is the running sum of those weights. The rule catalog (codes, default weights, descriptions) lives in `SuspicionType.cs`.
:::

## How suspicion is aggregated

Every detector ultimately calls one method on `ISuspicionService`:

```csharp
Task AddSuspicion(
    Participation participation,
    string ruleCode,
    string details,
    int? relatedParticipationId = null,
    CancellationToken token = default);
```

`SuspicionService.AddSuspicion` does three things:

1. Looks up the rule weight. It reads a `SuspicionRule` row matching `ruleCode`; if none exists it falls back to `SuspicionType.GetDefaultWeight(ruleCode)` (and ultimately to `10` for an unknown code).
2. **De-duplicates.** If a `SuspicionEvent` with the same `ParticipationId`, `Type`, and `Details` already exists, it returns without writing anything. This is why re-running the report does not inflate scores for already-known events.
3. Inserts the `SuspicionEvent` and adds its `ScoreDelta` (the weight) to `Participation.SuspicionScore`.

### Weights and signal tiers

Weights are defined in `SuspicionType.Defaults`. A selection relevant to this page:

| Rule code | Default weight | Meaning |
|---|---|---|
| `CrossTeamContainerAccess` | 120 | Non-admin user from a different team opened the proxy WebSocket on this team's container |
| `StolenFlag` | 100 | Flag stolen from another team |
| `FlagEgress` | 80 | Team's flag observed in proxied container traffic |
| `CrossTeamIP` | 20 | An IP used by members from multiple teams |
| `InstantSubmitAfterAccess` | 50 | Submission within seconds of the submitter's first proxy access |
| `DelayedSolveSubmission` | 40 | Submitter opened the container long before they submitted |
| `SubmitterNeverAccessedContainer` | 30 | A teammate, not the submitter, opened the container |
| `AccessIpMismatchAtSubmission` | 30 | Submitter's IP at submit time matches no IP they used to access the container |
| `SubnetOverlap` | 5 | Teams share the same /24 — soft amplifier |

Signals are graded into three tiers in `SuspicionType.cs`:

- **Hard** (`HardSignals`) — high-confidence evidence, always filed. Includes `StolenFlag`, `FlagEgress`, `CrossTeamContainerAccess`, `WrongFlagLeakage`, `NoContainer`, `NoDownload`, `TokenAbuse`, and the honeypot protocol/canary/chain hits.
- **Strong** (`StrongSignals`) — filed always; includes `CrossTeamIP`, `SequenceSimilarity`, `DelayedSolveSubmission`, `InstantSubmitAfterAccess`, the fast-solve signals, and others.
- **Soft** — anything that is neither Hard nor Strong (`SuspicionType.IsSoft` returns true), e.g. `SubnetOverlap`, `DirectedSolving`, `FirstBloodAnomaly`.

:::tip Tier gating prevents false reds
Inside the cheat report, soft signals are **dropped for a team unless that team also has at least one Hard or Strong signal**. This stops a pile of low-confidence soft signals (subnet overlap + directed solving + first-blood) from stacking into a false "red" team.
:::

## Detection signal: cross-team IP correlation

This family answers: *is the same IP being used to act on behalf of more than one team?* It is computed in `CheatReportController.Get` from the `Logs` table (rows whose `Logger` contains `AccountController`, with a non-null `RemoteIP` and `UserName`) plus each member's stored `UserInfo.IP`, all bounded to the game window (`StartTimeUtc`..`EndTimeUtc`, or "now" in practice mode).

The controller builds a `Team → Set<IP>` map and the reverse `IP → List<TeamId>`, then raises several distinct rule codes:

### `SharedIP` — same IP across multiple teams
For every IP observed under more than one team, a `SharedIP` event is filed against each team sharing it. The `Details` payload names the IP, the other source teams, and which users on each side used that IP.

### `CrossTeamIP` — attachment download from another team's IP
When a `Download` game event records a download source IP (`evt.Values[3]`) that the reverse map attributes to a *different* team, a `CrossTeamIP` event is raised against the downloading team, listing the source teams and source users.

### `UnknownIP` — download from an IP never seen for that team
If the download IP is parseable but is not in the team's own known-IP set and not attributable to any other team, an `UnknownIP` event is raised.

### `ClusteredRegistration` — shared first-login IP + tight registration window
Using each user's **first-ever** `AccountController` log IP (more accurate than `UserInfo.IP`, which is the last-login IP), accounts from multiple teams that share a registration IP **and registered within 48 hours** are flagged. Groups where more than 4 teams share the IP are suppressed (large shared NAT, e.g. a university).

### `SubnetOverlap` — shared /24 (soft)
Teams whose IPs fall in the same /24 are flagged with the soft `SubnetOverlap` signal (weight 5). Groups larger than 4 teams are suppressed. Computed by `GetSubnet28` (the masked-octet helper) and only meaningful when corroborated by a harder signal.

### `SessionConcurrency` — one account, two distant networks at once
The same username appearing from two IPs in **different /20 subnets** within a 10-minute window, **≥3 times**, is flagged. The `SameSubnet20` helper suppresses same-ISP-pool churn (mobile/DHCP), and the ≥3-occurrence requirement filters one-off VPN switches.

:::warning Honest boundary: this is login/download telemetry, not egress
Cross-team IP correlation here draws on **authenticated login logs and download events** — data the platform already attributes to a user and team. It does **not** correlate the *remote* IPs seen in egress traffic across teams, because egress data is only captured inside an authenticated team's own proxy session; there is no cross-team-visible IP stream to join on. Shared NAT (universities, corporate networks, single-exit VPNs) will produce `SharedIP`/`SubnetOverlap` noise — that is exactly why those are weighted low and why soft signals are tier-gated.
:::

## Detection signal: stolen-flag detection

"Stolen flag" means a flag turning up where it should not — submitted, attempted, or observed leaving a box it does not belong to. Several rule codes converge on this idea.

### `WrongFlagLeakage` — another team's valid flag submitted as a wrong answer
The report loads every per-team dynamic flag (`GameInstance.FlagContext.Flag`) and indexes flag → owning teams. It then scans **wrong** submissions: if a team submits, as a wrong answer, a string that is another team's *valid* dynamic flag, a `WrongFlagLeakage` event (weight 80, Hard) is raised. This catches near-misses where a stolen flag was already expired, destroyed, or mistyped — the flag value itself is the smoking gun.

### `FlagEgress` — the flag observed leaving the box (see traffic section)
The packet-level tracer raises `FlagEgress` when a team's own flag bytes are seen in that team's proxied container traffic. For **dynamic** flags this is strong evidence of an exfil pipeline or automated solver; for **static** flags it is recorded but does not bump the score (every solver trips the same bytes). Detailed below.

### `TokenAbuse` — download/submission token used by a different actor
Parsed from structured `Download` event metadata (or legacy `[Token Source: …]` log tags): when a token issued to one user/team is used by another actor, a Hard `TokenAbuse` event (weight 80) records actor, declared team, token type, and token source.

:::info `StolenFlag` (weight 100) is the strongest stolen-flag code
`StolenFlag` is a Hard signal in the catalog reserved for confirmed cross-team flag theft. The report's heuristic paths above (`WrongFlagLeakage`, `FlagEgress`, `TokenAbuse`) are the automatically-firing cousins; treat a high stolen-flag-family score as a single team's flag material being where it should not be.
:::

## Detection signal: container-access-based submission detection

This family correlates **who actually touched the box** against **who submitted the flag**, using the proxy access log. It only works when the container provider's `PortMappingType` is `PlatformProxy` — that is the mode where the platform terminates the connection and can observe access. With any other port-mapping mode there are no access events to correlate and the detector returns immediately.

### Capturing access: `ContainerAccessLogger`
Every successful proxy WebSocket open is described by a `ContainerAccessContext` (container id, challenge, owner participation, accessing user/participation, remote IP, user agent, admin flag, connect time) and passed to `IContainerAccessLogger.LogAccess`. When `CheatDetectionConfig.LogContainerAccess` is true (default), it persists a `ContainerAccessEvent` row.

It also raises **`CrossTeamContainerAccess`** (weight 120, Hard) immediately when an authenticated, non-admin user from a participation that is **not** the container owner opens the proxy — i.e. someone reached into another team's box. Admins/monitors are exempt (they legitimately access any container). The event is filed against the *owning* team's participation with `relatedParticipationId` pointing at the accessing participation, and the `Details` carries `accessingUser`, `accessingUserId`, `accessingPid`, `containerId`, and `remoteIp`.

### Correlating at submission time: `ContainerAccessSubmissionDetector`
On each accepted submission, `FlagChecker` invokes `RunChecks(submission, platformProxyEnabled, token)`. The detector loads all `ContainerAccessEvent` rows for the challenge that occurred at or before the submission, splits them into the submitter's own accesses and the team's accesses, and raises up to four signals:

| Signal | Fires when | Threshold (config) |
|---|---|---|
| `DelayedSolveSubmission` | Submitter opened the box, then submitted much later | latency > `DelayedSubmissionThresholdMinutes` (default 60) |
| `InstantSubmitAfterAccess` | Submitter submitted almost immediately after first access | latency < `InstantSubmitThresholdSeconds` (default 3) |
| `SubmitterNeverAccessedContainer` | Submitter never opened the box but a teammate did | submitter has 0 access rows, team has ≥1 |
| `AccessIpMismatchAtSubmission` | Submitter's IP at submit time matches none of the IPs they used to access | resolved via `IpAttributionHelper` within a 5s window |

`AccessIpMismatchAtSubmission` relies on `IpAttributionHelper.ResolveUserIpAt`, which joins the `Logs` table on `UserName` + `TimeUtc` to recover the most recent IP the user was seen using around the submission moment, then compares it against the distinct IPs from their access events.

:::tip Why these are well-grounded
`InstantSubmitAfterAccess` and `SubmitterNeverAccessedContainer` are the most actionable here: a flag submitted three seconds after first opening a remote box, or submitted by someone who never opened it while a teammate did, is hard to explain as legitimate solo play. Negative latency from cross-service clock skew is clamped to zero, so a few milliseconds of skew will not produce a false `InstantSubmitAfterAccess`.
:::

:::warning Boundaries of access correlation
- Requires `PlatformProxy` port mapping **and** `LogContainerAccess: true`. Otherwise no rows exist and nothing fires.
- Challenges that predate instrumentation, or that nobody accessed via the proxy, produce zero access rows — the detector simply returns (absence of access is not treated as evidence here).
- `DelayedSolveSubmission`/`InstantSubmitAfterAccess` need at least one **submitter** access row; the IP-mismatch check additionally needs a resolvable login IP within the 5-second window.
:::

## Detection signal: pcap / flow flag-egress inspection

When traffic capture is enabled for a challenge, the proxy records each TCP session to a gzipped pcap. This fork adds two complementary layers on top: a live byte-scanner and an offline flow viewer.

### Live tracer: `FlagEgressService` + `FlagEgressInspector`
`FlagEgressService` is a singleton that owns one inspector pair (egress + ingress) per recorder. A recorder is registered when a container with a per-team flag starts proxying. Registration is skipped unless `FlagEgressConfig.Enabled` is true and the flag is at least **4 bytes**.

`FlagEgressInspector.Inspect` scans each captured buffer for the exact flag byte sequence, keeping a `flagLen − 1` tail buffer so a flag split across two buffers is still found. On the hot path, `FlagEgressService.Inspect` skips packets smaller than `MinPacketDataLength` (default 8 bytes) before doing any work.

On a hit, hits are aggregated by `(participationId, challengeId, remoteIp, direction)` within `WindowSeconds` (default 60). The first hit of a window:

- Persists a `FlagEgressEvent` row (game, participation, challenge, container, remote IP/port, first/last seen, hit count, direction).
- For **dynamic** flags, raises a `FlagEgress` `SuspicionEvent` (weight 80) with details like `remoteIp=…:port direction=… container=…`.
- For **static** flags, records the row and broadcasts a live admin notice but **does not** raise suspicion (every successful team trips the same static bytes; only frequency anomalies are interesting).
- Broadcasts a `ReceivedFlagEgress` notice to admin clients in real time.

`HitCount`/`LastSeenUtc` updates are batched to the DB on a timer (`FlushIntervalSeconds`, default 5).

```yaml
# appsettings.json — section name matches the type name (FlagEgressConfig)
FlagEgressConfig:
  Enabled: true            # master toggle; false bypasses all scanning
  WindowSeconds: 60        # hit-aggregation window per (pid, challenge, ip, direction)
  MinPacketDataLength: 8   # skip tiny TCP control frames
  FlushIntervalSeconds: 5  # cadence for batching HitCount/LastSeen to DB

CheatDetectionConfig:
  LogContainerAccess: true            # write a ContainerAccessEvent per proxy open
  DelayedSubmissionThresholdMinutes: 60
  InstantSubmitThresholdSeconds: 3
```

Live egress events are listed for organizers via:

```bash
# Admin live feed — paged FlagEgressEvent rows for a game (RequireGameAdmin)
GET /api/admin/Games/{id}/FlagEgress?skip=0&count=50
```

Returns an `ArrayResponse<FlagEgressEventModel>`: team name, challenge title, remote IP/port, hit count, first/last seen, and direction (`ContainerToTeam` / `TeamToContainer`).

### Offline flow viewer: `PcapFlowExtractor`
For deep inspection, `PcapFlowExtractor` re-hydrates a recorded pcap into per-TCP-session "flows". The on-disk format is a synthetic Ethernet/IPv6/UDP wrapper where the UDP port carries a per-connection id in `[10001, 65000]` (a metadata frame on port 10000 is skipped); direction is inferred from which side holds the connection port. Two Monitor-gated endpoints expose it:

```bash
# List one summary per proxied TCP session in a capture (RequireMonitor)
GET /api/game/Captures/{challengeId}/{partId}/{filename}/Flows
    ?RegexPattern=...&PeerIpContains=...&Direction=...&FlagsOnly=true&StartUtc=...&EndUtc=...

# Full per-chunk payload (base64) for one connectionPort (RequireMonitor)
GET /api/game/Captures/{challengeId}/{partId}/{filename}/Flow/{connectionPort}
```

Each `TrafficFlowSummary` reports `PeerIp`, packets/bytes in and out, and **`FlagHits`** — the count of flag occurrences anywhere in that flow's payload. The detail endpoint returns every `TrafficFlowChunk` (direction, timestamp, base64 payload, and `FlagOffsets` marking where the flag begins). The flow list supports a `FlowFilter`:

| Filter field | Effect |
|---|---|
| `RegexPattern` | .NET regex over the ASCII rendering of the flow's combined payload (500 ms match timeout; invalid regex returns empty, not 500) |
| `PeerIpContains` | substring match on the peer IP |
| `StartUtc` / `EndUtc` | time-range overlap |
| `Direction` | `ContainerToTeam` or `TeamToContainer` |
| `FlagsOnly` | only flows with `FlagHits > 0` |

The flags scanned against are resolved from the specific participation's instance, so the viewer highlights *that team's* flag inside *that team's* capture.

:::warning Boundary: egress data is scoped to the team's own session
Both the live tracer and the flow viewer operate **inside a single team's proxied container traffic**. The flag bytes that match are that team's own per-team flag, observed at that team's proxy boundary. This is why egress evidence is excellent for "this team's flag left the box / an automated solver is pulling it" but cannot, on its own, prove *which other team* received it — the remote peer IP recorded is just the team-side endpoint, not an authenticated cross-team identity. Confirm exfil-to-another-team by pairing egress evidence with `WrongFlagLeakage`, `CrossTeamContainerAccess`, or sequence/relay correlation. Static flags also deliberately do not raise suspicion (only frequency anomalies indicate tooling), and flags shorter than 4 bytes are not traced at all.
:::

## Surfacing reports: `CheatReportController`

Organizers retrieve everything through one Monitor-gated controller, routed at `api/game/{id}/cheatreport`:

```bash
# Build/refresh the full cheat report for a game (RequireMonitor)
GET /api/game/{id}/cheatreport

# Compare two participations head-to-head: RSI + per-challenge solve-time deltas
GET /api/game/{id}/cheatreport/compare?participationA={pidA}&participationB={pidB}
```

`GET /cheatreport` recomputes the heuristic checks (IP correlation, abnormal solves, sequence/relay similarity, burst/automation, collusion grouping, first-blood anomalies, etc.), **persists** any new findings via `AddSuspicion`, and returns a `CheatReport` with these sections:

- **`IpAnalysis`** — IP/identity-correlation findings (`SharedIP`, `CrossTeamIP`, `UnknownIP`, `SharedFingerprint`, `FingerprintChurn`, `IpChurn`, `ClusteredRegistration`, `SubnetOverlap`, `SessionConcurrency`, `TokenAbuse`, `SequenceSimilarity`, `SolutionRelay`), each with a human-readable `Details` block and related teams/users.
- **`AbnormalSolves`** — solve-behavior findings (`NoDownload`, `NoContainer`, fast-solves, `Hoarding`, `ZeroWrongAttempts`, `WrongFlagLeakage`, `AdaptiveFastSolve`, `DirectedSolving`, `HighWrongRate`, `AutomatedPattern`, `Burst`, `FirstBloodAnomaly`).
- **`CollusionGroups`** — teams clustered by hybrid solve-set/solve-order similarity (RSI = 0.7·Jaccard + 0.3·LCS), each member also scored under `CollusionGroup`.
- **`SuspicionList`** — the authoritative per-participation rollup: team, status, total `SuspicionScore`, and every `SuspicionEvent` (type, weight delta, details, time), ordered by score descending. This is the list that includes events raised *outside* the report — `FlagEgress`, `CrossTeamContainerAccess`, and the four submission-time access signals all land here.

:::info The report is the heuristic layer; the live detectors run continuously
`CheatReportController` re-derives the log/submission heuristics on demand. The container-access and flag-egress detectors run **inline** (on each proxy open and each accepted submission), so their events appear in `SuspicionList` even before you open the report. Refreshing the report is idempotent for already-recorded events thanks to the de-dup in `AddSuspicion`.
:::

## Acting on a report

1. **Open the report** for the game (`GET /api/game/{id}/cheatreport`) and read `SuspicionList` top-down — it is sorted by total score, so the highest-suspicion participations surface first.
2. **Read the evidence, not just the number.** Each `SuspicionEvent.Details` is a structured key/value block (`label: value` lines, or `;`-separated `key:value` pairs for the access/egress signals). Prioritize **Hard** signals (`CrossTeamContainerAccess`, `StolenFlag`, `FlagEgress`, `WrongFlagLeakage`, `TokenAbuse`) — a soft-only score means the tier gate already judged it uncorroborated.
3. **Corroborate IP signals.** `SharedIP`/`SubnetOverlap`/`ClusteredRegistration` are NAT-prone; confirm against `CrossTeamContainerAccess` (someone literally reached into another team's box) or `WrongFlagLeakage` (another team's flag value submitted) before treating it as theft.
4. **Pull the traffic.** For a `FlagEgress` hit, open the team's capture and run the flow viewer with `FlagsOnly=true` (or a `RegexPattern` over the flag) to see the exact session, peer IP, direction, and the chunk where the flag appears (`FlagOffsets`).
5. **Compare suspected pairs** with `/cheatreport/compare` to get the RSI and the per-challenge solve-time deltas behind a `SequenceSimilarity`/`SolutionRelay`/`CollusionGroup` finding.
6. **Decide and apply a penalty** using your existing game-administration tools (e.g. disqualifying or adjusting a participation). The suspicion score is decision-support; the platform records evidence but does not auto-disqualify.

## Related

- [/guide/features/honeypots](/guide/features/honeypots) — the honeypot baits (`HoneypotHit`, `HoneypotProtocolHit`, `HoneypotCanaryFlag`, `HoneypotChain`) feed the same `SuspicionEvent` pipeline and appear in the same `SuspicionList`.
- [/guide/features/attack-defense](/guide/features/attack-defense) — A&D games rely heavily on the proxy-based access and flag-egress signals described here.
- [/guide/features/scoring](/guide/features/scoring) — how solve scoring interacts with the submissions these detectors analyze.
- [/config/appsettings](/config/appsettings) — the `FlagEgressConfig` and `CheatDetectionConfig` sections shown above.
