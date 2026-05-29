# King of the Hill

King of the Hill (KotH) is a game mode built on the same Attack & Defense engine. Instead of every team defending its own copy of a service, **all teams fight over a single shared container per challenge** — the *hill* — and the platform awards points to whichever team currently controls it. Control is proven by planting a per-round token into a marker file, not by capturing a platform-planted flag.

If you have read the [A&D engine](/guide/features/attack-defense) and [scoring](/guide/features/scoring) pages, most of the round/tick machinery here will be familiar: KotH reuses the same rounds, the same functional checker, and the same scoreboard cadence.

## How it differs from Attack & Defense

| Aspect | Attack & Defense | King of the Hill |
| --- | --- | --- |
| Containers | One per team per challenge | **One shared hill per challenge, for the whole game** |
| Flag | Platform plants a fresh flag in `/flag` each tick | **No platform flag.** Teams plant their own rotating token in `/koth/king` |
| Scoring unit | Per-team attack + SLA | Per-tick *hold credit* for the one controlling team |
| Checker role | Validates flag + functional SLA | **Flag-free** — functional health only; gates hold vs. penalty |
| Who scores | Many teams per tick | At most one team per tick (zero-sum race) |

The shared hill is launched once per `(game, challenge)` as a `KothTarget`, reachable by every team. There is no per-team isolation on the hill itself — any team that can reach it can read and write its filesystem (this matters for the attribution caveat below).

## The control marker: `/koth/king`

The platform decides the king by reading one file from the hill container each tick: **`/koth/king`**. It does not write this file — your team does, through an exploit. The flow each round is:

1. The round advances. The platform mints a fresh, secret control token for **your team** for that round.
2. You fetch your token from the API.
3. You exploit the hill and write your exact token bytes into `/koth/king`.
4. At the per-tick check, the platform reads `/koth/king`, trims whitespace, and matches it against the tokens issued for that round. Whoever's token matches is recorded as the controller.

### How the platform reads and matches

Each tick the checker reads the raw bytes of `/koth/king` straight from the container filesystem (via the Docker archive API, or `exec` on Kubernetes — no shell/coreutils required in the image), then:

```text
marker = UTF8(bytes(/koth/king)).Trim()
if marker is non-empty:
    match = KothToken where ChallengeId == this hill
                        and RoundNumber == current round
                        and Token == marker
    if match: controller = match.ParticipationId
```

So the match is **exact** (after trimming surrounding whitespace) against the token minted for *this* round. A token from a previous round will not match — it has rotated. An empty or absent file means "no controller this tick".

:::info
The read is exact-byte and shell-free, so your hill image does **not** need `cat`, `sh`, or any coreutils. Just make `/koth/king` writable through whatever vulnerability the challenge exposes.
:::

## Getting your control token

```text
GET /api/Game/{id}/Ad/Koth/{challengeId}/Token
```

Auth is the same dual scheme as A&D submit: a `Bearer ad_...` team API token (for scripted play) **or** the logged-in session cookie. The caller must be an accepted member of the game, and `{challengeId}` must be an enabled `KingOfTheHill` challenge in that game.

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
| `round` | The round this token is valid for (`0` = no round started yet) |
| `token` | The exact bytes to plant into `/koth/king`; `null` if none was minted for you |
| `status` | `ready` (plant it), `warmup` (no round yet), or `no-token-this-round` (round exists but your team wasn't accepted in time to be issued one — resolves next round) |

The token **rotates every round**. Tokens are minted at round-advance for every accepted team and every KotH challenge, inside the same DB transaction that makes the round visible — so the token is always available the moment the round is live. To keep holding the hill you must **re-fetch and re-plant each round**; last round's token is dead.

A scripted loop looks like:

```bash
GID=1; CID=42; TOKEN="ad_yourteamtoken"
BASE="https://gzctf.gzti.me/api/Game/$GID/Ad/Koth/$CID"

# 1. fetch this round's control token
KING=$(curl -s -H "Authorization: Bearer $TOKEN" "$BASE/Token" | jq -r .token)

# 2. plant it via your exploit (challenge-specific) — the goal is:
#    write "$KING" into /koth/king on the hill
./exploit.sh "$HILL_HOST" "$KING"

# 3. confirm the plant took effect without waiting for the scoreboard tick
curl -s -H "Authorization: Bearer $TOKEN" "$BASE/State" | jq
```

### Confirming control: `/State`

```text
GET /api/Game/{id}/Ad/Koth/{challengeId}/State
```

Returns the last persisted verdict so you can confirm a plant without polling the scoreboard (which only updates once per tick). Response (`KothHillStateModel`):

| Field | Meaning |
| --- | --- |
| `round` | Round the last result was scored for |
| `holderParticipationId` / `holderTeamName` | Who the platform currently records as king |
| `isYou` | True when your team is the recorded holder |
| `status` | Last functional verdict on the hill: `Ok` / `Mumble` / `Offline` |
| `checkedAt` | When that verdict was taken |
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
# their per-round token here.
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
