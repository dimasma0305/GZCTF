# Scoring

This page documents exactly how points are awarded across the three engines this fork runs: **Attack & Defense (A&D)**, **King of the Hill (KotH)**, and the upstream **jeopardy** dynamic-score board. Every formula and constant below comes straight from the code — primarily `src/GZCTF/Utils/AdScoring.cs` (the single source of truth for A&D + KotH), `src/GZCTF/Repositories/AdScoreboardRepository.cs` (how those formulas are summed into the board), and `src/GZCTF/Models/Data/GameChallenge.cs` (the jeopardy curve).

:::info
A&D and KotH have **separate, independent scoreboards** and never mix into one total. The A&D board (`GenScoreboardAsync`) only sums `AttackDefense` challenges; the KotH board (`GenKothScoreboardAsync`) only sums `KingOfTheHill` challenges. Jeopardy scoring is the stock GZ::CTF dynamic curve and is tracked on its own board. See [/guide/features/scoring](/guide/features/scoring) for the player-facing overview.
:::

## A&D: the three components

An A&D team's total per challenge is computed in `AdScoreboardRepository.GenScoreboardAsync`:

```csharp
var net = atkPts + sla - defLoss;
```

and the team `Total` across all enabled A&D challenges is:

```csharp
Total = tAttack + tSla - tDefense
```

So **Attack** and **SLA** add to your score, and **DefenseLoss** subtracts from it. Each is defined below.

### Attack points (capture, rarity-weighted pool)

Each flag (one per victim · service · round) is worth a fixed **pool** of points, **split equally among every team that steals it**. If `k` teams capture a given flag, each of them scores `AttackPool / k`. Stealing a flag only *you* found is worth the whole pool; a flag everyone steals is worth a sliver each. This rewards **exclusive exploitation** over brute-force volume, and caps inflation — a single flag injects at most one pool into the game no matter how many teams pile on.

From `AdScoring.cs`:

```csharp
public const double AttackPool = 1.0;

// One capturer's share of a flag that `capturers` teams stole in total.
public static double AttackShare(int capturers) =>
    capturers <= 0 ? 0.0 : AttackPool / capturers;
```

The formula in plain text:

```text
your share of a flag = AttackPool / k       (k = number of teams that stole that flag)
attack(team, challenge) = Σ over flags you stole of (AttackPool / k)
```

Because `k` depends on **later** captures (another team may steal the same flag after you), attack is **computed at scoreboard render** in `AdScoreboardRepository` — it loads the captures, counts the distinct capturers per flag (`k`), and sums `AttackPool / k` per (attacker, challenge). It is **not** frozen at capture time. The capture response (`AdGameController`) returns a *provisional* share, `AttackPool / (priorCapturers + 1)` — the upper bound on your final share, which shrinks as more teams capture the same flag. An advisory lock still serializes concurrent submitters so the capture order (used for that provisional value) is consistent.

Per-flag share table (with `AttackPool = 1.0`):

| Teams that stole the flag (`k`) | Each stealer's share | Flag's total payout |
| --- | --- | --- |
| 1 (only you) | 1.000 | 1.000 |
| 2 | 0.500 | 1.000 |
| 3 | 0.333 | 1.000 |
| 5 | 0.200 | 1.000 |
| 10 | 0.100 | 1.000 |

:::tip
The total payout column is flat — that's the cap. A flag is worth one pool, period. Finding a flag *others can't* (a unique exploit, better recon) is worth far more than farming a flag everyone already has.
:::

### Defense loss (penalty when your box is captured)

Defense is the **mirror of attack**: you lose `DefensePool` for each of your flags that leaked, counted **once per flag** regardless of how many teams stole it (matching the single pool those stealers split). At equal pools, the points attackers gained from your flags exactly equal the points you lost — field-level zero-sum between offense and defense.

```csharp
public const double DefensePool = 1.0;

// compromisedFlags = count of your DISTINCT flags that were stolen at least once.
public static double DefenseLoss(int compromisedFlags) =>
    DefensePool * compromisedFlags;
```

In plain text:

```text
DefenseLoss = DefensePool * (number of your distinct flags that leaked)
```

`compromisedFlags` counts your **distinct** leaked flags (`AdFlagId`) for that challenge, not raw capture rows — a flag stolen by five teams still cost you one flag's worth. The scoreboard subtracts this from your per-challenge net. Penalty table (with `DefensePool = 1.0`):

| Distinct flags leaked | DefenseLoss |
| --- | --- |
| 0 | 0 |
| 1 | 1 |
| 5 | 5 |
| 10 | 10 |
| 20 | 20 |

:::tip
**Pools and balance.** The three pillars are weighted by fixed per-event *pools* — `AttackPool`, `DefensePool`, and the per-tick SLA scale — which act as an exchange rate, not a ceiling. The default is **1·1·1** (equal). Because the rarity model makes a strong attacker's total attack land naturally on the same scale as a perfect-uptime team's SLA, the board self-balances with no per-game tuning. Raise `AttackPool` for an offense-led board; raise `DefensePool` to make getting popped bite harder (it can push a heavily-farmed team's total negative). They are fixed constants in `AdScoring.cs`, not per-game tunable.
:::

### SLA (service availability)

SLA rewards keeping your service **up and correct**, tick after tick. It is a **sum over ticks**, not a ratio — an Offline/Mumble/InternalError tick simply earns 0 and never retroactively drags down a ratio.

Each tick the checker assigns one of four verdicts, mapped to per-tick credit:

```csharp
public const double SlaCreditOk         = 1.0;  // up + correct this tick
public const double SlaCreditRecovering = 0.5;  // Ok this tick, but down/mumble last tick
public const double SlaCreditNone       = 0.0;  // Mumble / Offline / InternalError
```

The verdict-to-credit mapping (`AdScoring.TickCredit`):

```csharp
public static double TickCredit(AdCheckStatus current, AdCheckStatus? previous) =>
    current switch
    {
        AdCheckStatus.Ok when previous is AdCheckStatus.Offline or AdCheckStatus.Mumble
            => SlaCreditRecovering,           // 0.5 — recovering
        AdCheckStatus.Ok => SlaCreditOk,      // 1.0 — clean Ok
        _ => SlaCreditNone                    // 0.0 — Mumble/Offline/InternalError
    };
```

| Verdict this tick | Verdict last tick | Per-tick credit |
| --- | --- | --- |
| `Ok` | `Ok` (or none) | 1.0 |
| `Ok` | `Offline` or `Mumble` | 0.5 (recovering) |
| `Mumble` | any | 0.0 |
| `Offline` | any | 0.0 |
| `InternalError` | any | 0.0 |

:::info
`InternalError` is a **checker/infra fault** (pruned checker image, network failure, container failed to start) — not the team's fault. Like a down tick it earns no credit, but because SLA is a **sum** it cannot pull your score down; it just fails to add. This was a deliberate fix to an older ratio model that unfairly penalized teams for checker faults they didn't cause.
:::

#### Field-size scaling

To keep SLA comparable to attack points as the field grows, each tick's credit is multiplied by `sqrt(activeTeams)` — but this is folded in **at the moment the check lands**, using that round's accepted-team count, then stored on the row:

```csharp
// SlaFieldFactor(activeTeams) = sqrt(max(1, activeTeams))
public static double SlaFieldFactor(int activeTeams) => Math.Sqrt(Math.Max(1, activeTeams));
```

Storing the field-scaled credit per row (`SlaCredit`, summed into `SlaCreditTotal`) means a later roster change (a team accepted or rejected) can't retroactively rescale a team's whole SLA history. The scoreboard then only applies the flat per-tick scale:

```csharp
public const double SlaPerTickScale = 1.0;

// creditSum is already field-scaled; this is just the flat per-tick scale.
public static double SlaPoints(double creditSum) => creditSum * SlaPerTickScale;
```

Full SLA in plain text:

```text
per-tick stored credit = TickCredit(verdict) * sqrt(max(1, activeTeams))   // computed when the check lands
SLA points             = (Σ stored per-tick credit) * SlaPerTickScale      // SlaPerTickScale = 1.0
```

So a team that is `Ok` for 100 consecutive ticks in a 9-team game earns `100 * 1.0 * sqrt(9) = 300` SLA points.

:::tip
The live scoreboard reads a precomputed running total (`AdTeamServices.SlaCreditTotal`), so it never scans the unbounded check-results table. A **frozen** view (after `FreezeTimeUtc`) instead sums the individual `AdCheckResults.SlaCredit` rows as-of the freeze cutoff. Both paths produce the same number; the live one is just cheaper.
:::

### Worked A&D example

One challenge, 9 accepted teams (`sqrt(9) = 3`), pools `1·1·1`. Team A over the game:

- Stole 6 flags: 4 that only they found (`k=1` → 1.0 each) and 2 that one other team also got (`k=2` → 0.5 each) → `4 * 1.0 + 2 * 0.5 = 5.0` attack.
- 3 of their own flags leaked → `1.0 * 3 = 3.0` defense loss.
- Service `Ok` for 80 of 100 ticks (and never recovering) → `80 * 1.0 * 3 = 240.0` SLA.

```text
Net = Attack + SLA - DefenseLoss
    = 5.0    + 240.0 - 3.0
    = 242.0
```

(SLA dominates here only because this team is on the board every tick but stole few flags. A heavy attacker's `Σ AttackPool/k` climbs into the same range as SLA — that scale-match is exactly what the rarity pool buys you.)

## KotH: hold points per tick

King of the Hill has its **own** board. There is one shared container per KotH challenge (`KothTarget`); every team attacks the same box, and each tick at most one team "controls" the hill. Scoring is a clean per-tick race — only the controller can score that tick — so, unlike SLA, there is **no field-size scaling** (only one team scores per tick, so field size dilutes nothing).

### Hold credit, penalty, and grace

From `AdScoring.cs`:

```csharp
public const double KothBrokenHillPenalty = 1.0;

// Flat hold points; NO team-count scaling.
public static double KothHoldPoints(double holdPointsPerTick) => holdPointsPerTick;

public static (double HoldCredit, double Penalty) KothTickDelta(
    bool hasKing, AdCheckStatus status, double holdPointsPerTick,
    bool freshlyElected = false)
{
    if (!hasKing)
        return (0.0, 0.0);
    if (status == AdCheckStatus.Ok)
        return (KothHoldPoints(holdPointsPerTick), 0.0);
    var holderAtFault = status is AdCheckStatus.Mumble or AdCheckStatus.Offline;
    return holderAtFault && !freshlyElected
        ? (0.0, KothBrokenHillPenalty)
        : (0.0, 0.0);
}
```

`holdPointsPerTick` comes from the game's `KothHoldPointsPerTick` (`Game.cs`), which **defaults to `1.0`**. The per-tick rules:

| Hill state this tick | Result `(HoldCredit, Penalty)` | Net delta |
| --- | --- | --- |
| No king (no one controls it) | `(0, 0)` | 0 |
| King, hill functional (`Ok`) | `(holdPointsPerTick, 0)` | `+holdPointsPerTick` (default `+1`) |
| King, hill broken (`Mumble`/`Offline`), **not** freshly elected | `(0, KothBrokenHillPenalty)` | `−1.0` |
| King, hill broken, **freshly elected** (grace) | `(0, 0)` | 0 |
| King, hill `InternalError` (checker/infra fault) | `(0, 0)` | 0 |

The KotH board sums `HoldCredit − Penalty` per (team, hill), restricted to ticks the team controlled:

```csharp
var pts = earned - penalty;   // per cell
total += pts;                 // per team, across hills
```

:::info
**Broken-hill penalty.** If you hold the hill but you've **broken the box you control** (a `Mumble` or `Offline` verdict you're responsible for), you are debited a flat `KothBrokenHillPenalty = 1.0` that tick. `InternalError` is exempt — same reasoning as SLA: a checker/infra fault must never debit whoever happens to hold the hill.
:::

:::tip
**One-tick election grace.** A team that *just* took the marker (different controller from the previous tick → `freshlyElected = true`) gets one grace tick on the broken-hill penalty: they inherited the previous holder's damage and haven't had time to fix it, so a broken hill yields `(0, 0)` instead of `(0, penalty)`. From the second tick they hold onward, they're on the hook normally. The scoreboard distinguishes a real broken tick from a grace tick by counting only ticks that actually produced `Penalty > 0` (`BrokenTicks`).
:::

### The refresh wipe

The shared KotH container is reset to its base image every `Game.KothRefreshTicks` ticks (**default `5`**), tracked by `KothTarget.LastRefreshRound`. The refresh wipes whatever patches/footholds teams had established on the box, so control is contestable again from a clean state. Refresh also happens immediately if an operator flips `AdAllowEgress` for the challenge mid-game (detected via `LaunchedWithEgress` drift), rather than waiting for the next refresh boundary.

:::warning
The refresh resets the **container**, not the **scores**. Hold credit and penalties already banked in prior ticks remain on the board — the wipe only levels the playing field for who can take the hill going forward.
:::

## Jeopardy: the dynamic score curve

Standard (non-A&D, non-KotH) challenges use GZ::CTF's dynamic exponential-decay curve. As more teams solve a challenge, its value drops toward a floor. From `GameChallenge.cs`:

```csharp
internal static int CalculateChallengeScore(int originalScore, double minScoreRate, double difficulty,
    int acceptedCount)
{
    if (acceptedCount <= 1)
        return originalScore;

    return (int)Math.Floor(
        originalScore *
        (minScoreRate + (1.0 - minScoreRate) * Math.Exp((1 - acceptedCount) / difficulty)));
}
```

In plain text:

```text
if acceptedCount <= 1:
    score = OriginalScore
else:
    score = floor( OriginalScore *
            ( MinScoreRate + (1 - MinScoreRate) * exp( (1 - acceptedCount) / Difficulty ) ) )
```

The three inputs are per-challenge fields (with these defaults from `GameChallenge.cs`):

| Field | Default | Meaning |
| --- | --- | --- |
| `OriginalScore` | `1000` | Full value with ≤1 solve |
| `MinScoreRate` | `0.25` | Floor as a fraction of `OriginalScore` (here, 250) |
| `Difficulty` | `5` | Larger = slower decay |

With the defaults (`OriginalScore=1000`, `MinScoreRate=0.25`, `Difficulty=5`):

| Accepted solves | Current score |
| --- | --- |
| 1 | 1000 |
| 2 | 864 |
| 5 | 586 |
| 10 | 373 |
| 20 | 266 |
| 50 | 250 (floor) |

:::info
A&D and KotH challenges are explicitly skipped by this curve. In `GameRepository`, `info.Score` is left at 0 for `AttackDefense`/`KingOfTheHill` types — they have no first-blood-decay scoring and use the engines above instead.
:::

These per-challenge knobs are set in the challenge YAML / editor; see [/guide/authoring/challenge-yaml](/guide/authoring/challenge-yaml). Example:

```yaml
type: StaticAttachment
originalScore: 1000
minScoreRate: 0.25
difficulty: 5
```

### First-blood bonuses (5% / 3% / 1%)

The first three scoring-eligible solvers of a jeopardy challenge get a multiplicative bonus on the challenge's **current** (decayed) score. The bonus is packed into `Game.BloodBonus` (`Utils/Shared.cs`):

```csharp
public const long DefaultValue = (50 << 20) + (30 << 10) + 10;  // First=50, Second=30, Third=10
// Each factor is bonus/1000 + 1.0:
//   FirstBloodFactor  = 50/1000 + 1.0 = 1.05  (+5%)
//   SecondBloodFactor = 30/1000 + 1.0 = 1.03  (+3%)
//   ThirdBloodFactor  = 10/1000 + 1.0 = 1.01  (+1%)
```

Applied in `GameRepository` when building the scoreboard:

```csharp
SubmissionType.FirstBlood  => Convert.ToInt32(challenge.Score * bloodFactors[0]),  // ×1.05
SubmissionType.SecondBlood => Convert.ToInt32(challenge.Score * bloodFactors[1]),  // ×1.03
SubmissionType.ThirdBlood  => Convert.ToInt32(challenge.Score * bloodFactors[2]),  // ×1.01
SubmissionType.Normal      => challenge.Score,
```

| Solve rank | Default factor | Bonus |
| --- | --- | --- |
| 1st (first blood) | 1.05 | +5% |
| 2nd | 1.03 | +3% |
| 3rd | 1.01 | +1% |
| 4th and later | 1.00 | none |

:::tip
The bonus multiplies the **decayed** score at solve time, not the original. The three blood factors are configurable per game via `BloodBonus`; the table above shows the defaults. A solver that is not score-eligible does **not** consume a blood slot — that would unfairly downgrade the bonus tier of legitimately-scoring solvers after it. Set `disableBloodBonus: true` on a challenge to turn bonuses off entirely for it.
:::

The blood defaults live alongside the rest of the game/server defaults; see [/config/appsettings](/config/appsettings).

## Tie-breaking

All three boards break ties **deterministically**, but by different keys.

**A&D and KotH** rank by total descending, then by `ParticipationId` ascending — a stable, deterministic tie-break for equal totals (`AdScoreboardRepository`):

```csharp
.OrderByDescending(r => r.Total)
.ThenBy(r => r.ParticipationId)  // stable, deterministic tie-break for equal Totals
```

**Jeopardy** ranks by score descending, then by **earliest last-eligible-submission time** ascending — reaching a given score sooner ranks higher (`GameRepository`):

```csharp
.OrderByDescending(i => i.Score)
.ThenBy(i => i.LastSubmissionTime)
.ThenBy(i => i.Id)  // final deterministic key (team id) on a full tie
```

Only **scoring-eligible** solves update `LastSubmissionTime`, so an ineligible late submission can never push a team's tie-break time later and unfairly rank it below an earlier eligible team. When both score and submission time are equal, `Id` (team id) is the final fallback that makes the ordering fully deterministic.

| Board | Primary | Tie-break |
| --- | --- | --- |
| A&D | `Total` (desc) | `ParticipationId` (asc) |
| KotH | `Total` (desc) | `ParticipationId` (asc) |
| Jeopardy | `Score` (desc) | `LastSubmissionTime` (asc), then `Id` (asc) |
```
