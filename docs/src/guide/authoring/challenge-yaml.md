# challenge.yml Reference

Every challenge in this platform is described by a single `challenge.yaml` (or `challenge.yml`) file at the root of its package directory. The same file is read in three situations: an admin uploads a one-shot tarball/zip, a public GitHub repo is imported, and a [repo binding](/guide/authoring/repo-bindings) re-syncs a checkout. All three paths share one parser, so the schema below is identical everywhere.

This page documents the exact fields the importer understands, where each value lands, and what is deliberately ignored. Everything here is derived from `ChallengeYamlModel` and `ChallengeImportService` in the server — not from the upstream gzcli docs, which differ in places.

:::info
The file may be named either `challenge.yaml` or `challenge.yml`. The importer treats the directory that contains it as the **package root**: paths like `provide:` and `./src/Dockerfile` are resolved relative to that directory.
:::

## Key conventions

- **Keys are camelCase.** The deserializer is built with `CamelCaseNamingConvention`. A key like `container_image` (snake_case) is silently dropped — use `containerImage`. This applies to nested blocks too (`memoryLimit`, `cpuCount`, `checkerImage`, `allowEgress`, …).
- **Unknown keys are ignored, not errors.** The parser runs with `IgnoreUnmatchedProperties()`, so a typo or an upstream gzcli field we don't map simply has no effect. There is no validation warning for an unrecognized key — double-check spelling against the tables below.
- **Some template keys have no effect.** `value:` and `visible:` are not fields the importer maps — like any unrecognized key they are silently dropped by `IgnoreUnmatchedProperties()`. They exist in templates only so the file round-trips with gzcli; scoring and visibility stay admin-controlled.
- **A missing `containerImage` triggers an auto-build.** For any container-type challenge, if `container.containerImage` is omitted (or is a gzcli template placeholder like `{{.slug}}:latest`), the platform looks for `./src/Dockerfile`, then `./Dockerfile`, and builds it. See [Auto-build behavior](#auto-build-behavior).

## Top-level keys

These keys sit at the root of `challenge.yml`.

| Key | YAML type | Maps to / effect | Default if omitted |
| --- | --- | --- | --- |
| `name` | string | Challenge title. **Required** — a file with no `name` is skipped. The title is also the upsert key: re-importing the same `name` into the same game updates the existing challenge rather than creating a duplicate. | — (skipped) |
| `author` | string | Prepended to the description as `Author: **<author>**` when set. | none |
| `description` | string | Challenge body (`Content`). Markdown and HTML are supported. | empty |
| `category` | string | Parsed to a `ChallengeCategory` enum (case-insensitive). If omitted/unknown, inferred from the parent directory name (up to 3 levels up), else falls back to `Misc`. | inferred → `Misc` |
| `type` | enum string | Challenge type — see [the type enum](#the-type-enum). **Required**; an unknown value skips the challenge. | — (skipped) |
| `value` | int | **IGNORED on import.** Points are admin-controlled. New challenges inherit `OriginalScore = 1000`; existing rows keep whatever the admin set. | n/a |
| `flags` | string list | Static flags. Synced additively — flags in the file that aren't already on the challenge are added. | none |
| `flagTemplate` | string | Dynamic-flag template (leetspeak/UUID-style). Note `container.flagTemplate` takes precedence over a top-level `flagTemplate` if both are set. | unchanged |
| `hints` | string list | Hints shown to players. | none |
| `submissionLimit` | int | Max submissions per team for this challenge. | unchanged |
| `disableBloodBonus` | bool | Disable first-/second-/third-blood bonus for this challenge. | unchanged |
| `minScoreRate` | double | Floor of the dynamic-score decay, as a fraction. **Clamped to `[0.0, 1.0]`** on import. | unchanged |
| `difficulty` | double | Steepness of the dynamic-score decay curve. **Clamped to a minimum of `0.01`** (`Math.Max(0.01, …)`); non-positive values would invert the curve. | unchanged |
| `provide` | string | Relative path to a single attachment file **or** a directory. A directory is tar+gzipped into one archive (256 MB cap). Path must stay inside the package (no `..`, no absolute paths). | none |
| `ignore` | bool | When `true`, the challenge is never created or updated. Lets an operator delete a repo-sourced challenge in the admin UI without it resurrecting on the next sync. Takes precedence over everything else, including an invalid `type`. | `false` |
| `container` | block | Container settings — see [The `container:` block](#the-container-block). | none |
| `ad` | block | A&D / KotH engine settings — see [The `ad:` block](#the-ad-block). | none |

:::warning
`value:` is not a mapped field — it is silently ignored like any unknown key, so it **never affects** `OriginalScore`. Scoring is admin-controlled in the UI or the admin scoring API. Putting `value: 500` in the file does nothing — see [Scoring](/guide/features/scoring). Likewise `visible:` is ignored; only an admin flips `IsEnabled`.
:::

### The `type` enum

`type` is parsed case-insensitively into the `ChallengeType` enum. Accepted values:

| `type` value | Meaning |
| --- | --- |
| `StaticAttachment` | One shared attachment + flag for all teams. |
| `StaticContainer` | One shared container + flag for all teams. |
| `DynamicContainer` | Per-team container, per-team flag injected via env. |
| `DynamicAttachment` | Per-team attachment carrying a per-team flag. |
| `AttackDefense` | Attack & Defense. Persistent per-team service container for the whole game; per-tick attack/defense/SLA scoring. |
| `KingOfTheHill` | King of the Hill. One **shared** hill container all teams race to control; per-tick hold scoring. |

`AttackDefense` and `KingOfTheHill` both report `IsContainer() == true` and run on the shared A&D engine (`UsesAdEngine() == true`). That is why both reuse the `container:` block for their service image and honor the `ad:` block.

:::info
For A&D and KotH the importer does **not** require a `flags:` list or a `flagTemplate:` — those types are exempt from the flag-source submission check. A&D plants a fresh per-team flag into the container each tick; KotH has no flag at all (the platform reads `/koth/king`).
:::

## The `container:` block

Used by all container types, including `AttackDefense` and `KingOfTheHill`. Every key is optional; an omitted key leaves the existing value untouched (so a sparse block only overrides what it names). Container fields are applied **only** when `type` is a container type.

| Key | YAML type | Maps to | Notes / default |
| --- | --- | --- | --- |
| `containerImage` | string | `ContainerImage` | A registry ref (`nginx:alpine`, `ghcr.io/org/img:tag`) **or** a local Dockerfile path (`./src`, `./Dockerfile`, `./src/Dockerfile`). Omitting it (or a `{{…}}` placeholder) triggers an auto-build — see below. |
| `memoryLimit` | int (MiB) | `MemoryLimit` | Per-container memory cap. |
| `cpuCount` | int | `CPUCount` | CPU allocation. |
| `storageLimit` | int (MiB) | `StorageLimit` | Per-container storage cap. |
| `exposePort` | int | `ExposePort` | Port the service listens on inside the container. |
| `flagTemplate` | string | `FlagTemplate` | Dynamic-flag template. **Takes precedence over** a top-level `flagTemplate`. |
| `networkMode` | enum string | `NetworkMode` | Parsed case-insensitively to the `NetworkMode` enum; an unparseable value is silently left unchanged. |
| `enableTrafficCapture` | bool | `EnableTrafficCapture` | Enable PCAP traffic capture for this challenge. |

```yaml
container:
  exposePort: 80
  memoryLimit: 256
  cpuCount: 1
  storageLimit: 256
  # containerImage: ghcr.io/your-org/web-chal:latest   # or omit to auto-build ./src/Dockerfile
  # flagTemplate: "flag{[GUID]}"
  # networkMode: Isolated   # one of: Open | Isolated | Custom
  # enableTrafficCapture: false
```

### Auto-build behavior

When the challenge is a container type, the importer resolves a build intent from `containerImage`:

1. **Local path** (`./src`, `./Dockerfile`, `/…`, anything ending in `/Dockerfile`) → resolve the build context and build. If the referenced Dockerfile doesn't exist, the challenge is flagged `MissingDockerfile`.
2. **Template placeholder** (`containerImage` contains `{{`, e.g. `{{.slug}}:latest`) **or omitted** → look for `./src/Dockerfile`, then `./Dockerfile`. If found, build it; the platform fills in the image tag. If a placeholder was given but no Dockerfile exists, it's a `MissingDockerfile` error.
3. **Registry ref** (`nginx:alpine`, `ghcr.io/foo:tag`) → pulled as-is, no build.

```yaml
# Auto-build: no containerImage, Dockerfile lives at ./src/Dockerfile
container:
  exposePort: 8080
  memoryLimit: 512
```

:::tip
The conventional layout is a `./src/Dockerfile` next to `challenge.yml`. With that in place you can omit `containerImage` entirely and the platform builds the image for you. See [Templates](/guide/authoring/templates) for the full package layout.
:::

:::warning
Only **trusted** imports auto-build immediately. Admin uploads and repo-binding syncs run with auto-approve and build right away. A player-submitted package's attacker-controlled Dockerfile is **not** built on the shared host until an admin approves it — the challenge stays pending in the review queue.
:::

## The `ad:` block

The `ad:` block carries the **per-challenge** Attack & Defense / King of the Hill knobs. It is consulted only when `type` is `AttackDefense` **or** `KingOfTheHill` (`UsesAdEngine()`), and it is sparse-friendly: an omitted key keeps the current value. The service image and ports come from the shared `container:` block — `ad:` only carries the engine-specific switches.

| Key | YAML type | Maps to | Notes |
| --- | --- | --- | --- |
| `checkerImage` | string | `AdCheckerImage` | Checker image speaking the enochecker3 exit-code contract (`0 Ok / 1 Mumble / 2 Offline / 3 InternalError`). Trimmed; only applied when non-empty. **Omit** to fall back to a plain TCP-reachability probe (no flag-correctness check). Must be a **pushed registry ref** — see warning below. |
| `allowEgress` | bool | `AdAllowEgress` | Can the service reach the public internet? For KotH this also selects the network mode the shared hill launches under (`false` → Isolated). |
| `allowSelfReset` | bool | `AdAllowSelfReset` | Can teams self-reset this service's container? For KotH this must stay `false` (the hill is shared). |

```yaml
ad:
  checkerImage: "ghcr.io/your-org/attack-defense-checker:latest"
  allowEgress: true
  allowSelfReset: true
```

:::danger
`checkerImage` must be a **registry reference that has actually been pushed** (e.g. `ghcr.io/your-org/checker:latest`), not a local-only tag. The cluster/daemon pulls it fresh, and local-only images are reaped by image-prune jobs — a pruned checker image makes every tick go `InternalError`. Build `./checker` and push it before referencing it.
:::

:::info
**Event-wide** A&D policy is NOT set here. Tick length, flag lifetime, warmup, reset cooldown, snapshot toggles, and the checker timing fractions (`getflagWindowFraction`, `minGracePeriodSeconds`) are configured **once per event** in the `ad:` block of the `.gzevent` manifest (or admin game settings), because rounds span the whole game and every service shares one tick. KotH hold scoring (`KothHoldPointsPerTick`, `KothRefreshTicks`) is currently fixed at defaults on the `Game` row. Putting any of these on a single challenge has no effect. See [appsettings / config](/config/appsettings).
:::

## Complete example: Attack & Defense

```yaml
# yaml-language-server: $schema=https://raw.githubusercontent.com/dimasma0305/gzcli/refs/heads/main/internal/template/templates/others/ctf-template/.gzctf/challenge.schema.yaml

name: "attack-defense"
author: "your-name"

description: |
  Example Attack & Defense service. The platform plants a fresh flag into
  /flag every tick; your service must expose it only to whoever holds the
  intended capability, and defenders patch the bug without breaking the
  checker.

type: "AttackDefense"
value: 1000              # ignored on import — points are admin-controlled

# A&D reuses the container block for the per-team SERVICE image + port.
# containerImage omitted -> platform auto-builds ./src/Dockerfile.
container:
  exposePort: 80
  memoryLimit: 256
  cpuCount: 1
  storageLimit: 256

ad:
  # checkerImage: "ghcr.io/your-org/attack-defense-checker:latest"  # registry ref; omit -> TCP probe
  allowEgress: true       # can the service reach the internet?
  allowSelfReset: true    # may a team reset its own container?
```

## Complete example: King of the Hill

```yaml
# yaml-language-server: $schema=https://raw.githubusercontent.com/dimasma0305/gzcli/refs/heads/main/internal/template/templates/others/ctf-template/.gzctf/challenge.schema.yaml

name: "king-of-the-hill"
author: "your-name"

description: |
  Example King of the Hill challenge. A single SHARED service — the "hill" —
  that every team races to control. Each round the platform issues a fresh
  control token; exploit the hill to write that token into /koth/king. The
  platform reads /koth/king every tick: while it holds your token AND the hill
  is still functional (checker Ok), you earn hold points.

type: "KingOfTheHill"
value: 1000              # ignored on import — KotH is hold-scored, not flag-scored

# ONE shared hill container for the whole game (not one per team).
# containerImage omitted -> platform auto-builds ./src/Dockerfile.
container:
  exposePort: 80
  memoryLimit: 256
  cpuCount: 1
  storageLimit: 256

ad:
  # checkerImage: "ghcr.io/your-org/king-of-the-hill-checker:latest"  # health-only probe
  allowEgress: false      # the hill is a target; it almost never calls out
  allowSelfReset: false   # MUST stay false — the hill is shared across all teams
```

:::tip
KotH needs no `flags:` or `flagTemplate:`. The platform determines the king by reading the `/koth/king` marker file from the hill, and uses the checker purely as a health probe (`Ok` = working, `Mumble` = degraded, `Offline` = down).
:::

## See also

- [Templates](/guide/authoring/templates) — the package layout, `./src/Dockerfile`, `./checker`, and `solver/` conventions.
- [Repo bindings](/guide/authoring/repo-bindings) — keeping challenges in sync from a GitHub repo.
- [Scoring](/guide/features/scoring) — how points, decay (`minScoreRate` / `difficulty`), and blood bonuses actually work, given that `value:` is ignored.
