# King of the Hill

King of the Hill (KotH) is a game mode built on the same Attack & Defense engine. Instead of every team defending its own copy of a service, **all teams fight over a single shared container per challenge** — the *hill* — and the platform awards points to whichever team currently controls it. Control is proven by planting a control token into a marker file, not by capturing a platform-planted flag. Each team's token is **game-wide** (one value works on every hill) and rotates only when the hills reset, so a team fetches it once per refresh window and plants it on whichever hills it captures.

If you have read the [A&D engine](/guide/features/attack-defense) and [scoring](/guide/features/scoring) pages, most of the round/tick machinery here will be familiar: KotH reuses the same rounds, the same functional checker, and the same scoreboard cadence.

## How it differs from Attack & Defense

| Aspect | Attack & Defense | King of the Hill |
| --- | --- | --- |
| Containers | One per team per challenge | **One shared hill per challenge, for the whole game** |
| Flag | Platform plants a fresh flag in `/flag` each tick | **No platform flag.** Teams plant their own game-wide control token in `/koth/king` |
| Scoring unit | Per-team attack + SLA | Per-tick *hold credit* for the one controlling team |
| Checker role | Validates flag + functional SLA | **Flag-free** — functional health only; gates hold vs. penalty |
| Who scores | Many teams per tick | At most one team per tick (zero-sum race) |

The shared hill is launched once per `(game, challenge)` as a `KothTarget`, reachable by every team. There is no per-team isolation on the hill itself — any team that can reach it can read and write its filesystem (this matters for the attribution caveat below).

## The control marker: `/koth/king`

The platform decides the king by reading one file from the hill container each tick: **`/koth/king`**. It does not write this file — your team does, through an exploit. The flow is:

1. At each refresh-window boundary (every `KothRefreshTicks` rounds, when the hills reset), the platform mints one fresh, secret control token for **your team**. It is game-wide — the same value works on every hill — and stays stable for the whole window.
2. You fetch your token from the API (once per window is enough).
3. You exploit each hill you want and write your exact token bytes into `/koth/king`.
4. At the per-tick check, the platform reads `/koth/king`, trims whitespace, and matches it against the token issued for the current refresh window. Whoever's token matches is recorded as the controller.

### How the platform reads and matches

Each tick the checker reads the raw bytes of `/koth/king` straight from the container filesystem. On Docker this uses the daemon's tar archive API (no shell/coreutils needed in the image); on Kubernetes it execs a small `sh -c` reader, so a K8s hill image must contain `sh`, `head`, `base64`, and `tr`. Then:

```text
marker = UTF8(bytes(/koth/king)).Trim()
anchorRound = ((currentRound - 1) / KothRefreshTicks) * KothRefreshTicks + 1
if marker is non-empty:
    match = KothToken where RoundNumber == anchorRound          # the window's anchor, not the current round
                        and Token        == marker
                        and Participation.GameId == this game   # game-wide token; NOT filtered by hill
    if match: controller = match.ParticipationId
```

So the match is **exact** (after trimming surrounding whitespace) against the token minted for the current refresh window. The lookup is **not** scoped to this hill — the token is game-wide, so the same value controls whichever hill it is planted in. A token from a previous window will not match (it has rotated at the reset); an empty or absent file means "no controller this tick".

:::info
Under the **Docker** provider the read is exact-byte and shell-free — your hill image needs no `cat`, `sh`, or coreutils to be read. Under **Kubernetes** the platform reads the marker with `sh -c 'head -c N /koth/king | base64 | tr ...'` exec'd inside the hill container, so on K8s the image **must** contain `sh`, `head`, `base64`, and `tr`; otherwise the read fails and no controller is recorded that tick. Either way, just make `/koth/king` writable through whatever vulnerability the challenge exposes.
:::

## Getting your control token

The token is game-wide, so the preferred endpoint needs **no challenge id**:

```text
GET /api/Game/{id}/Ad/Koth/Token
```

Auth is the same dual scheme as A&D submit: a `Bearer ad_...` team API token (for scripted play) **or** the logged-in session cookie. The caller must be an accepted member of the game, and the game must have at least one enabled `KingOfTheHill` challenge.

> A per-challenge form `GET /api/Game/{id}/Ad/Koth/{challengeId}/Token` still exists for backward compatibility and returns the **same** game-wide token (the `{challengeId}` only validates that the hill exists). Prefer the id-free form above.

Response (`KothTokenModel`):

```json
{
  "round": 7,
  "token": "koth_9Qm3...redacted",
  "status": "ready"
}
```

| Field | Meaning |
| --- | --- |
| `round` | The window-anchor round this token belongs to (`0` = no round started yet) |
| `token` | The exact bytes to plant into `/koth/king`; `null` if none was minted for you |
| `status` | `ready` (plant it), `warmup` (no round yet), or `no-token-this-round` (a round exists but your team wasn't accepted in time to be issued one — resolves at the next refresh) |

The token **rotates only at the refresh-window boundary** (every `KothRefreshTicks` rounds, default 5 — the same moment the hills reset), not every round. One token is minted per accepted team per window and stays stable across the intervening ticks, so you fetch it once after a reset and plant it on whichever hills you take; re-fetch only when the hills reset.

A basic plant sequence looks like:

```bash
GID=1; TOKEN="ad_yourteamtoken"
BASE="https://gzctf.gzti.me/api/Game/$GID/Ad"

# 1. fetch your control token (game-wide — no challenge id needed)
KING=$(curl -s -H "Authorization: Bearer $TOKEN" "$BASE/Koth/Token" | jq -r .token)

# 2. plant it via your exploit (challenge-specific) — the goal is:
#    write "$KING" into /koth/king on each hill you want to hold
./exploit.sh "$HILL_HOST" "$KING"

# 3. confirm the plant took effect without waiting for the scoreboard tick (see /Koth/Hills below)
curl -s -H "Authorization: Bearer $TOKEN" "$BASE/Koth/Hills" | jq '.[] | {hill: .title, isYou, status}'
```

### See every hill at once: `/Koth/Hills`

```text
GET /api/Game/{id}/Ad/Koth/Hills
```

One call returns every enabled hill's holder, target, and status — the list form of per-challenge `/State`, and the recommended way to confirm plants and feed a bot all targets (no challenge id). Response is a JSON array of `KothHillStateModel`:

```jsonc
[
  {
    "challengeId": 220,
    "title": "Blockchain Hill",
    "round": 42,
    "holderParticipationId": 7,
    "holderTeamName": "team-name",
    "isYou": true,            // your team currently holds this hill
    "status": "Ok",           // Ok / Mumble / Offline / InternalError / null
    "ip": "10.0.1.5",         // where to aim (null until a container exists)
    "port": 31000,
    "lastRefreshRound": 40    // round the hill was last wiped
  }
]
```

### Confirming a single hill: `/State`

```text
GET /api/Game/{id}/Ad/Koth/{challengeId}/State
```

Returns the same fields as one `/Koth/Hills` element, for a single hill (`KothHillStateModel`):

| Field | Meaning |
| --- | --- |
| `round` | Round the last result was scored for |
| `holderParticipationId` / `holderTeamName` | Who the platform currently records as king |
| `isYou` | True when your team is the recorded holder |
| `status` | Last functional verdict on the hill: `Ok` / `Mumble` / `Offline` / `InternalError` (or `null` until the first check is scored) |
| `checkedAt` | When that verdict was taken |
| `ip` / `port` | The hill's current container address (populated by `/Koth/Hills`) |
| `lastRefreshRound` | The round the hill was last wiped (see refresh below) |

## Scoring: the checker gates hold vs. penalty

KotH runs the **same functional checker as A&D, but flag-free** — it only probes health (the enochecker3 exit-code contract: `0 Ok / 1 Mumble / 2 Offline / 3 InternalError`, or the built-in TCP-reachability probe if no checker image is set). It is *not* given a flag to validate. Its verdict decides whether the recorded king earns points or eats a penalty.

The per-tick delta (`AdScoring.KothTickDelta`) is `(HoldCredit, Penalty)`, and the scoreboard sums **`HoldCredit − Penalty`** per team:

| King present? | Functional verdict | Result | Reasoning |
| --- | --- | --- | --- |
| No matching token | — | `(0, 0)` | Nobody controls the hill |
| Yes | `Ok` | `(+KothHoldPointsPerTick, 0)` | You hold a working hill — score |
| Yes | `Mumble` / `Offline` | `(0, KothBrokenHillPenalty)` | You broke the box you hold — penalty (`1.0` flat) |
| Yes | `InternalError` | `(0, 0)` | Checker/infra fault (e.g. pruned image, network) — never debited to whoever happens to hold the hill |

Hold credit is a **flat** value per tick — there is no `sqrt(teams)` scaling like SLA. KotH is a zero-sum race for one marker, so only one team scores per tick and field size dilutes nothing: "hold one tick = +1" by default.

:::warning Holding a hill you have wrecked costs you points
If your exploit knocks the service into `Mumble`/`Offline`, you keep "control" of the marker but eat the broken-hill penalty every tick until you fix it or lose the marker. The intended play is: pop the box, plant your token, and **keep it serving**.
:::

### Freshly-elected grace (2-tick lookback)

A team that *just* took the hill shouldn't be punished for damage the previous holder left behind. So a controller is treated as **freshly elected** — and gets a one-tick grace (`(0, 0)` instead of the penalty) on a broken hill — only if they controlled the hill in **neither of the previous two ticks** (`round − 1` and `round − 2`).

```csharp
var recentControllers = await db.KothControlResults
    .Where(r => r.ChallengeId == challenge.Id
        && (r.AdRound.Number == latest.Number - 1
            || r.AdRound.Number == latest.Number - 2))
    .Select(r => r.ControllingParticipationId)
    .ToListAsync(token);
freshlyElected = recentControllers.All(c => c != cid);
```

The two-tick window (not one) is deliberate: keying grace on "differs from `N−1`" alone let a team re-arm grace every other tick — drop the marker for one tick, or alternate with a colluder — to dodge the broken-hill penalty forever. Looking back two ticks denies that oscillation. Once you hold into a second consecutive tick, you are on the hook for the penalty normally.

## The N-tick refresh that wipes the hill

Every `KothRefreshTicks` rounds the hill is **reset to its base image** — destroyed and relaunched — wiping all footholds, patches, and the `/koth/king` marker. You must re-exploit the fresh box to start planting again.

The refresh fires *between* rounds, on the transition **into** the round after a boundary, so the team that held the hill at the boundary round still has its result scored before the wipe. For the default `KothRefreshTicks = 5`: rounds 1–5 use the original hill, the refresh fires entering round 6, rounds 6–10 use the first refreshed hill, and so on (rounds 6, 11, 16, …).

To stop whoever was winning from instantly re-popping the brand-new box, at each refresh the **recent leader** for that challenge gets a one-tick network cooldown: their VPN source IP(s) are dropped to the hill on the WireGuard sidecar for the next tick, then lifted. "Recent leader" is the team with the highest `HoldCredit − Penalty` *since the last refresh* (the window that just ended), so the punishment rotates to whoever is actually leading now rather than permanently throttling an early front-runner. Ties block all tied leaders; nobody scoring in the window blocks nobody.

:::warning Cooldown is Docker-only
The leader cooldown is implemented for the Docker container provider. On Kubernetes there is no NetworkPolicy parity yet, so the front-runner can re-pwn the freshly reset hill with no tick of network block — KotH on K8s is best-effort. The platform logs a one-time warning per game.
:::

## Configuration knobs

KotH adds two event-wide scoring knobs on the `Game` record:

| Field | Default | Meaning |
| --- | --- | --- |
| `KothHoldPointsPerTick` | `1.0` | Flat points the controlling team earns per held tick |
| `KothRefreshTicks` | `5` | Ticks between hill resets (clamped to a minimum of `1`) |

:::warning These two knobs are DB-only right now
`KothHoldPointsPerTick` and `KothRefreshTicks` are **not** exposed in the admin game-settings UI and are **not** read from the `.gzevent` manifest. To change them you must update the `Games` row directly in the database. The defaults (`1.0` / `5`) apply otherwise.
:::

### `allowSelfReset` must be false for a shared hill

KotH borrows the per-challenge container block and A&D flags. One of those, `allowSelfReset`, lets a team reset *its own* A&D container. Because the hill is **shared across all teams**, a self-reset would let any team wipe everyone else's footholds and the current marker at will.

:::danger
Set `allowSelfReset: false` on a King of the Hill challenge. There is no per-team container to reset — exposing self-reset on a shared hill hands every team a button to nuke the round for everyone.
:::

## A minimal hill challenge

A KotH challenge uses `type: KingOfTheHill` and the standard container block. There is **no** `/flag` bind-mount and no pull-sidecar — the hill carries no platform flag. The image just needs the intended vulnerability and a `/koth` directory whose `king` file an attacker can write.

```yaml
title: "Notes Hill"
type: KingOfTheHill
container:
  exposePort: 80
  memoryLimit: 256
  cpuCount: 1
  storageLimit: 256
ad:
  # Functional checker (flag-free for KotH — health only). Omit for a
  # TCP-reachability probe.
  # checkerImage: "ghcr.io/your-org/notes-hill-checker:latest"
  allowEgress: false      # sandbox the shared hill unless it needs the internet
  allowSelfReset: false   # REQUIRED for a shared hill
```

```dockerfile
FROM alpine:3.21
RUN apk add --no-cache socat
# The hill the platform reads each tick. Make this writable through the
# challenge's intended bug; the platform never writes it — teams plant
# their control token here.
RUN mkdir -p /koth && touch /koth/king
COPY serve.sh /serve.sh
RUN chmod +x /serve.sh
EXPOSE 80
CMD ["socat", "-T", "5", "TCP-LISTEN:80,reuseaddr,fork", "SYSTEM:/serve.sh"]
```

See the king-of-the-hill template on the [challenge authoring templates](/guide/authoring/templates) page for a fuller starting point, and the [challenge.yml reference](/guide/authoring/challenge-yaml) for every field.

## Known limitation: control attribution

The controller is **whichever team's token matches the marker bytes** — the platform cannot tell *who physically wrote them*. Because every team can read the shared hill, a team that observes another team's token in `/koth/king` can replay it. The replay credits the **original bearer** (no score is transferred to the replayer), but it lets the replayer force a specific victim to stay recorded as controller.

Mitigations in place:

- The leader cooldown uses the recent-window definition, so force-feeding a victim controller status only rotates the cooldown to them briefly, not permanently.
- Every persisted result logs `(round, challenge, controller, matched token id)` at `Information` level, giving organizers a post-game audit trail (e.g. cross-referencing a team credited every tick against their WireGuard handshake logs).

A full fix would need an out-of-band write-source channel (image-level source-IP logging, or a packet-capture sidecar on the hill bridge) and is tracked as future work — it is not implementable inside the checker.

## Related pages

- [Scoring](/guide/features/scoring) — how hold credit and penalties roll into the board.
- [Challenge templates](/guide/authoring/templates) — the king-of-the-hill starter template.
- [challenge.yml reference](/guide/authoring/challenge-yaml) — the container/`ad` fields used above.
- [appsettings / configuration](/config/appsettings) — VPN sidecar and container-provider settings the cooldown relies on.
