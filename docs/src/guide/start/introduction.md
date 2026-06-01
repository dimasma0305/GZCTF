# Introduction

GZ::CTF is an open-source Capture-The-Flag platform built on **ASP.NET Core**. Upstream it gives organizers highly customizable jeopardy challenges (Static Attachment, Dynamic Attachment, Static Container, Dynamic Container), dynamic scoring with first-blood bonuses, on-demand container distribution over **Docker or Kubernetes**, real-time scoreboard and submission monitoring over SignalR, TCP-over-WebSocket traffic proxying, Redis/PostgreSQL backends, and object-storage support. For anything that ships in upstream unchanged, the canonical reference is the official documentation at [gzctf.gzti.me](https://gzctf.gzti.me/).

This site documents **a fork** of that platform. The fork keeps the entire jeopardy platform intact and adds a **live, tick-based competition engine** on top of it: an **Attack & Defense (A&D)** mode and a **King of the Hill (KotH)** mode. These are the additions documented here — everything else behaves as upstream.

:::info
This documentation covers **the fork's additions only**. The platform core remains AGPLv3 ([GZTimeWalker/GZCTF](https://github.com/GZTimeWalker/GZCTF)); see [License](#license) below.
:::

:::tip Two repos you'll use
The docs are built around two companion repositories:

- **[gzctf-platform-template](https://github.com/TCP1P/gzctf-platform-template)** — the deploy scaffold. An interactive wizard plus `make` targets bring up GZCTF (the published image) behind Traefik + Let's Encrypt TLS, with config in `appsettings.json`. Start here: [Quick start](/guide/start/quick-start).
- **[TCP1PADTesting](https://github.com/TCP1P/TCP1PADTesting)** — ready-to-import example challenges (two A&D, two KotH; OWASP web + PWN). You import it via an admin **Repo Binding** to populate a game. See [Repo bindings](/guide/authoring/repo-bindings).
:::

## What this fork adds

The fork introduces two new challenge types alongside the four jeopardy types. Both are defined in `src/GZCTF/Utils/Enums.cs` as members of the `ChallengeType` enum:

| Type | Enum value | Shape | One-line summary |
| --- | --- | --- | --- |
| `AttackDefense` | `0b100` (4) | One container **per team** | Each team gets a persistent service container that lives for the whole game; teams attack each other's services and defend their own. |
| `KingOfTheHill` | `0b101` (5) | **One shared** container | All teams attack a single shared container; whoever's rotating token sits in the marker controls "the hill" each tick. |

Both types run on the same shared infrastructure — referred to throughout this documentation as the **AdEngine**: rounds (ticks), live checkers, per-tick scoring, and a dedicated scoreboard. The two modes differ only in topology (per-team service vs. one shared hill) and in how points are awarded each tick.

### Attack & Defense

From the `ChallengeType.AttackDefense` definition in `Enums.cs`:

> Each team gets a persistent container per A&D challenge that lives for the whole game; teams attack each other's services on a shared network, defend their own, and score on attack + defense + SLA per tick.

A&D doesn't fit the historical static/dynamic × attachment/container matrix, so the type helpers treat it explicitly (see [The mental model](#the-mental-model) below).

:::tip
Teams reach their own and opponents' A&D services over a **WireGuard VPN**, so the live network is exposed only to participants in the game.
:::

### King of the Hill

From the `ChallengeType.KingOfTheHill` definition in `Enums.cs`:

> ONE shared container per challenge that all teams attack; each tick the team whose rotating token sits in the marker (`/koth/king`) controls it and earns hold points, and is penalized if the service breaks. Every few ticks the hill resets to base and the per-challenge score leader is briefly network-blocked. Reuses the A&D round/checker/scoreboard engine — handled by explicit cases like A&D.

So KotH reuses the same round/checker/scoreboard machinery as A&D, but instead of a service per team there is a single hill that teams fight to occupy.

### The per-tick checker contract

Each tick, the engine runs a checker against the relevant service container(s). The checker reports one of four statuses, defined by the `AdCheckStatus` enum in `Enums.cs` (compatible with the **enochecker3** contract):

| Status | Value | Meaning |
| --- | --- | --- |
| `Ok` | 0 | Checker succeeded — flag planted and retrieved. |
| `Mumble` | 1 | Service is up but behaving incorrectly (flag mismatch, partial outage, etc.). |
| `Offline` | 2 | Service didn't respond at all (TCP refused, timeout). |
| `InternalError` | 3 | The checker itself failed (bug in checker code, host died). |

## The mental model

The most important idea for organizers: **a single game can mix all three families of challenge at once** — jeopardy challenges, A&D challenges, and KotH challenges — within the same game and the same scoreboard infrastructure. Nothing forces a game to be "an A&D game" or "a jeopardy game."

What decides whether a given challenge is driven by the live engine is a single helper, `UsesAdEngine()`, defined on `ChallengeType` in `src/GZCTF/Utils/Enums.cs`:

```csharp
/// <summary>
/// Does it run on the shared A&D engine (rounds, ephemeral checker,
/// per-tick scoring, scoreboard) — Attack & Defense or King of the Hill.
/// </summary>
public bool UsesAdEngine() => type is ChallengeType.AttackDefense or ChallengeType.KingOfTheHill;
```

If `UsesAdEngine()` is true, the challenge is routed to the AdEngine; otherwise it follows the normal jeopardy path. The two new types also participate in the existing type predicates, which were extended to recognize them:

```csharp
// A&D is per-team (dynamic-ish) and uses containers, so
// IsDynamic() and IsContainer() both return true for it (and for KotH).

public bool IsDynamic() => type is ChallengeType.DynamicAttachment or ChallengeType.DynamicContainer
    or ChallengeType.AttackDefense or ChallengeType.KingOfTheHill;

public bool IsContainer() => type is ChallengeType.StaticContainer or ChallengeType.DynamicContainer
    or ChallengeType.AttackDefense or ChallengeType.KingOfTheHill;

public bool IsAttackDefense() => type is ChallengeType.AttackDefense;
public bool IsKingOfTheHill() => type is ChallengeType.KingOfTheHill;
```

The practical consequences of these helpers:

| Helper | `AttackDefense` | `KingOfTheHill` | Why it matters |
| --- | --- | --- | --- |
| `IsStatic()` | false | false | Neither uses a single shared flag-set. |
| `IsDynamic()` | true | true | Both are treated as per-team/dynamic. |
| `IsAttachment()` | false | false | Neither is an attachment challenge. |
| `IsContainer()` | true | true | Both spin up containers, so container-management paths apply. |
| `UsesAdEngine()` | true | true | **The routing switch** into the live engine. |

:::warning
`AttackDefense` and `KingOfTheHill` use enum values `0b100` (4) and `0b101` (5), which fall **outside** the original 2-bit `static|dynamic × attachment|container` layout used by the four jeopardy types. They are deliberately handled by explicit cases in `ChallengeTypeExtensions` rather than by bit math. If you add code that switches on `ChallengeType`, make sure to handle these two values explicitly.
:::

When authoring, you choose the engine for a challenge by setting its `ChallengeType` to `AttackDefense` or `KingOfTheHill` (the values are serialized as those strings). For example, in a challenge definition:

```yaml
type: AttackDefense   # or: KingOfTheHill
```

See [Challenge YAML](/guide/authoring/challenge-yaml) and [Templates](/guide/authoring/templates) for the full authoring schema, and [appsettings](/config/appsettings) for engine-wide configuration.

## Where to next

| Page | What you'll find |
| --- | --- |
| [Quick start](/guide/start/quick-start) | **Stand it up** — deploy GZCTF with the `gzctf-platform-template` wizard, then import the example challenges and run your first live-engine game. |
| [Attack & Defense](/guide/features/attack-defense) | Per-team service topology, VPN access, attack/defense/SLA scoring. |
| [King of the Hill](/guide/features/king-of-the-hill) | The shared hill, the `/koth/king` marker, hold points, resets, and leader blocking. |
| [Scoring](/guide/features/scoring) | How per-tick AdEngine scoring works alongside jeopardy dynamic scoring. |
| [Templates](/guide/authoring/templates) | Ready-made challenge skeletons, including enochecker3-compatible checkers. |

## License

The core platform is licensed under the **GNU Affero General Public License v3.0 (AGPLv3)** as published by the upstream project, [GZTimeWalker/GZCTF](https://github.com/GZTimeWalker/GZCTF). This documentation describes only the A&D and KotH additions made by this fork; for upstream behavior, configuration, and any restricted-component terms, consult the upstream project and its official documentation at [gzctf.gzti.me](https://gzctf.gzti.me/).

:::info
Deploying a **modified** GZ::CTF as a public service carries upstream attribution obligations (retain copyright/attribution, state the version and license, and link to the original repository). Review those requirements in the upstream README before going live.
:::
