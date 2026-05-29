# Configuration Reference

This page is the authoritative list of every knob the A&D / KotH fork exposes, grouped by where it lives:

- **App config** — process-level settings read from `appsettings.json` or environment variables at startup (database, Redis, container backend, the `Ad:*` infrastructure keys, `XorKey`).
- **Per-game** — event-wide A&D / KotH policy stored on the `Game` row; most are editable in the admin **Game → Info** form.
- **Per-challenge** — the `ad:` block on an individual A&D challenge.

Every default below is taken from the code. Where a value is read straight from configuration with no strongly-typed model, that is stated.

:::info
The A&D and KotH engine is a fork addition. The upstream jeopardy keys (account policy, container lifetime, telemetry, mail, captcha) are unchanged and only the ones relevant to running an A&D/KotH event are summarised here. See also [/guide/features/attack-defense](/guide/features/attack-defense), [/guide/features/king-of-the-hill](/guide/features/king-of-the-hill), and [/guide/features/scoring](/guide/features/scoring).
:::

---

## App-level configuration

App config binds from `appsettings.json` (and `appsettings.{Environment}.json`) or, in containerised deploys, from environment variables using the standard .NET double-underscore nesting convention (`Section__SubKey`). The fork's reference deploy sets everything via the `environment:` block in `docker-compose.yml`; the JSON shapes below are equivalent.

### Connection strings

| Key | Required | Example |
| --- | --- | --- |
| `ConnectionStrings:Database` | Yes | `Host=postgres;Database=gzctf;Username=gzctf;Password=gzctf` (PostgreSQL) |
| `ConnectionStrings:Redis` | Recommended for multi-instance / caching | `redis:6379,password=gzctf` |

```json
{
  "ConnectionStrings": {
    "Database": "Host=postgres;Database=gzctf;Username=gzctf;Password=gzctf",
    "Redis": "redis:6379,password=gzctf"
  }
}
```

As environment variables (compose style):

```bash
ConnectionStrings__Database=Host=postgres;Database=gzctf;Username=gzctf;Password=gzctf
ConnectionStrings__Redis=redis:6379,password=gzctf
```

### XorKey

`XorKey` is the master obfuscation key. It XOR-protects every secret stored at rest — the per-game Ed25519 signing private key (see `Game.GenerateKeyPair` / `Game.Sign`), the API encryption / token key pairs, and the XOR-obfuscated passwords in the admin config (build registry, SMTP, pull registry, captcha secret).

```bash
XorKey=gzctf-xor-key
```

:::danger
The platform refuses to start if `XorKey` is unset (`PrelaunchHelper` exits fatally with `Init_XorKeyNotSet`). Set it to a strong random value and treat it like a database master key: rotating it invalidates every secret encrypted under the old key, including game signing keys.
:::

### Container provider

The `ContainerProvider` section selects and configures the backend that launches challenge containers. It maps to the `ContainerProvider` model in `Models/Internal/Configs.cs`.

| Key | Type / values | Default | Meaning |
| --- | --- | --- | --- |
| `ContainerProvider:Type` | `Docker` \| `Kubernetes` | `Docker` | Which backend launches containers. Flipping this relaunches all challenge containers fresh. |
| `ContainerProvider:PortMappingType` | `Default` \| `PlatformProxy` | `Default` | `Default` maps the container port to a random host port; `PlatformProxy` proxies the container TCP port over a WebSocket (`wss`) through the platform. |
| `ContainerProvider:PublicEntry` | string | `""` (empty) | Host/IP players use to reach mapped container ports. Surfaced in connection info. |
| `ContainerProvider:EnableTrafficCapture` | bool | `false` | Master switch for per-container traffic capture (per-challenge capture also exists). |
| `ContainerProvider:DockerConfig` | object | — | Used when `Type=Docker` (see below). |
| `ContainerProvider:KubernetesConfig` | object | — | Used when `Type=Kubernetes` (see below). |

#### Docker sub-config (`ContainerProvider:DockerConfig`)

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `Uri` | string | `""` | Docker daemon endpoint. Empty + a mounted `/var/run/docker.sock` uses the local socket. |
| `UserName` | string? | null | Registry username for pulling private challenge images. |
| `Password` | string? | null | Registry password / PAT. |
| `ChallengeNetwork` | string? | null | Base name of the bridge network challenge containers attach to. |
| `EnforceEgressIsolation` | bool | `true` | Installs `DOCKER-USER` rules so a popped A&D container can't pivot team→team or reach cloud metadata / private ranges. Legit paths (checker, VPN ingress, internet on `open`) are exempted. Set `false` to disable; rules are removed on the next cycle. |

```yaml
# docker-compose.yml — Docker backend
environment:
  - ContainerProvider__Type=Docker
  - ContainerProvider__PortMappingType=PlatformProxy
  - ContainerProvider__EnableTrafficCapture=true
  - ContainerProvider__DockerConfig__ChallengeNetwork=challenges
  # EnforceEgressIsolation defaults to true; set false only to debug.
```

:::tip
On the Docker backend the A&D `/flag` file is delivered by a read-only bind mount from the host (the `ad-flags` volume), so container-root can't tamper with it. No pull sidecar is involved. See [/guide/deployment/provider](/guide/deployment/provider).
:::

#### Kubernetes sub-config (`ContainerProvider:KubernetesConfig`)

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `Namespace` | string | `gzctf-challenges` | Namespace challenge / checker pods run in. |
| `KubeConfig` | string | `kube-config.yaml` | Path to the kubeconfig file (mounted read-only). |
| `AllowCidr` | string[]? | null | Extra egress-deny CIDRs for `open` challenges. **Augments** the built-in private + link-local baseline (`10/8`, `172.16/12`, `192.168/16`, `169.254/16`); it does not replace it. |
| `Dns` | string[]? | null | DNS servers injected into challenge pods. |
| `ImagePullPolicy` | string | `Always` | Pull policy for launched pods. Use `IfNotPresent` for single-node / air-gapped clusters that side-load images (e.g. `k3d image import`). |
| `VerifyNetworkPolicy` | bool | `true` | At startup, spawns a throwaway isolated-labeled pod and verifies the CNI actually enforces `NetworkPolicy` (a CNI that ignores it turns all A&D isolation into a no-op). Best-effort; never blocks boot. |
| `NetworkProbeImage` | string | `busybox:stable` | Image used by the `VerifyNetworkPolicy` probe pod (needs a shell + busybox `nc`). |

```yaml
# docker-compose.yml — Kubernetes backend
environment:
  - ContainerProvider__Type=Kubernetes
  - ContainerProvider__KubernetesConfig__KubeConfig=/app/kube-config.yaml
  - ContainerProvider__KubernetesConfig__Namespace=gzctf-challenges
  - ContainerProvider__KubernetesConfig__ImagePullPolicy=IfNotPresent
  - ContainerProvider__KubernetesConfig__AllowCidr__0=172.0.12.0/24
```

:::warning
On the Kubernetes backend the `/flag` is delivered by a pull sidecar that fetches it over HTTP, so `Ad:FlagPullBaseUrl` (below) must be set to an **IP** — challenge pods use external DNS and can't resolve cluster/host names. There is also no in-place retain-restart and only egress challenges work end-to-end on virtual nodes.
:::

### `Ad:*` infrastructure keys

These configure the A&D supporting sidecars (flag delivery, WireGuard VPN, SSH jump host, checker concurrency). They are read directly from `IConfiguration` at runtime (e.g. `IConfiguration["Ad:Vpn:ConfigDir"]`, `IConfiguration["Ad:FlagPullBaseUrl"]`) — there is no strongly-typed config class, so set them exactly as named.

| Key | Default (compose) | Meaning |
| --- | --- | --- |
| `Ad:FlagPullBaseUrl` | `http://172.0.12.1:8080` | **Kubernetes only.** Base URL team pods' pull sidecar fetches the planted flag from. Must be an IP. If unset on K8s, flags can't be delivered. |
| `Ad:Vpn:ConfigDir` | `/wg-config` | Directory where the WireGuard sync service reads `server.pub`/`server.key` and writes `wg0.conf`. Must match the wireguard sidecar's `/config` mount. |
| `Ad:Vpn:ClientCidr` | `10.13.37.0/24` | Address pool VPN clients are allocated from. |
| `Ad:Vpn:ServerEndpoint` | `1pc.tf:51820` | Host-reachable UDP endpoint advertised to WG clients. Override per host. |
| `Ad:Ssh:InternalSecret` | `dev-only-rotate-me-before-prod` | Shared secret authenticating the ssh-jump sidecar to gzctf's internal lookup/exec endpoints. The same value is set on the ssh-jump container. |
| `Ad:Ssh:PublicHost` | `1pc.tf` | Hostname players SSH to. |
| `Ad:Ssh:PublicPort` | `22022` | Public SSH port (host) forwarded to port 22 inside the ssh-jump container. |
| `Ad:Checker:MaxParallel` | `10` | How many checker container runs are in flight at once. Raise if SLA badges lag the round. |
| `Ad:Checker:TimeoutSeconds` | `30` | Hard timeout per checker run. |

```yaml
environment:
  - Ad__FlagPullBaseUrl=http://172.0.12.1:8080      # K8s only
  - Ad__Vpn__ConfigDir=/wg-config
  - Ad__Vpn__ClientCidr=10.13.37.0/24
  - Ad__Vpn__ServerEndpoint=1pc.tf:51820
  - Ad__Ssh__InternalSecret=$(openssl rand -hex 32)  # rotate before prod
  - Ad__Ssh__PublicHost=1pc.tf
  - Ad__Ssh__PublicPort=22022
  - Ad__Checker__MaxParallel=10
  - Ad__Checker__TimeoutSeconds=30
```

:::danger
`Ad:Ssh:InternalSecret` is the only thing standing between the ssh-jump sidecar and gzctf's internal lookup/exec endpoints. The shipped default (`dev-only-rotate-me-before-prod`) must be replaced for any real deployment — generate one with `openssl rand -hex 32` and set the same value on both the gzctf and ssh-jump containers.
:::

For the full deployment topology of these sidecars, see [/guide/deployment/docker](/guide/deployment/docker).

---

## Per-game A&D settings (admin → Game → Info)

These are stored as columns on the `Game` row (`Models/Data/Game.cs`) and are **event-wide**: every A&D service in the game shares one tick, one flag lifetime, one warm-up, and so on. Rounds span the whole game.

Each field is editable in the admin **Game → Info** form via `GameInfoModel`. The fields are nullable in the edit model and only overwrite the stored value when the caller actually supplies one (`Game.Update` does `if (model.X is { } v) X = v;`), so leaving a field blank keeps the existing value.

| Field (`Game.*`) | Edit-model field | Type | Default | Meaning |
| --- | --- | --- | --- | --- |
| `AdWarmupSeconds` | `AdWarmupSeconds` | int? | `1800` (30 min) | Warm-up window from `StartTimeUtc` before round 1. Teams SSH in and write initial patches without scoring or attacks counting. |
| `AdTickSeconds` | `AdTickSeconds` | int? | `60` | Seconds per tick — the global scoring unit. The checker runs once per (team, service) per tick and flags rotate at tick boundaries. |
| `AdFlagLifetimeTicks` | `AdFlagLifetimeTicks` | int? | `5` | How many ticks a planted flag stays valid for attack submission — the uniform attack window. |
| `AdResetCooldownMinutes` | `AdResetCooldownMinutes` | int? | `5` | Minimum minutes between a team's self-resets (anti-spam fairness). Whether a service can be reset at all is the per-challenge flag. |
| `AdGetflagWindowFraction` | `AdGetflagWindowFraction` | double? | `0.5` | Getflag jitter window as a fraction of the tick, applied after the min grace period. A fresh random offset is rolled per (team, service, round) so the SLA-check instant can't be predicted. |
| `AdMinGracePeriodSeconds` | `AdMinGracePeriodSeconds` | int? | `3` | Seconds after a round starts (flags planted) before getflag may fire. Capped at half the tick by the scheduler. |
| `AdSnapshotRetentionDays` | `AdSnapshotRetentionDays` | int? | `null` (keep forever) | How long to retain per-team container snapshot tarballs after game end. Any positive integer = expire after N days; null = never expire. |
| `AdAllowSnapshotDownload` | `AdAllowSnapshotDownload` | bool | `true` | If true, each team's final container state is committed and saved as a gzipped tarball at game end and offered for download. Pairs with `AdSnapshotRetentionDays`. |

:::warning
The XML doc comment on `Game.AdTickSeconds` mentions 120 as an industry norm, but the actual code default is **60** (`AdTickSeconds = 60`). The other defaults match their comments. Trust the table above — these values are read straight from the field initialisers in `Game.cs`.
:::

### Runtime-only A&D state (not in the Info form)

Two `Game` fields are toggled by the engine / operator at runtime rather than configured ahead of time and are **not** part of `GameInfoModel`:

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `AdScoringPaused` | bool | `false` | When true, the round scheduler stops advancing rounds and the checker stops recording results — freezes flag rotation and SLA accrual without tearing anything down. Operator-toggled mid-event (e.g. infra incident). |
| `AdScoringPausedAt` | DateTimeOffset? | `null` | Instant scoring was paused. On resume, the current round's `StartedAt`/`EndsAt` are shifted forward by the paused duration so the round keeps its full remaining time. |

---

## KotH scoring knobs (DB-only)

The two King of the Hill tuning parameters live on the `Game` row but are **not** exposed in `GameInfoModel` / the admin Game → Info form, and are not carried in the `.gzevent` manifest. Changing them requires a direct edit of the `Game` row in the database.

| Field (`Game.*`) | Type | Default | Meaning |
| --- | --- | --- | --- |
| `KothHoldPointsPerTick` | double? | `1.0` | Flat base points the controlling team earns per tick of control. NOT scaled by team count — unlike SLA (KotH is a zero-sum, one-marker-per-challenge race, so field size doesn't dilute it). Event-wide. |
| `KothRefreshTicks` | int? | `5` | Ticks between hill resets. Every Nth tick the shared container is reset to its base image (wiping footholds + the control marker) and the current per-challenge score leader is network-blocked from that hill for one tick. Event-wide. |

:::warning
Because these are DB-only, they survive `.gzevent` re-scans untouched and cannot be set from the admin UI. To change them, update the `Game` row directly, e.g.:

```sql
UPDATE "Games"
SET "KothHoldPointsPerTick" = 2.0,
    "KothRefreshTicks" = 8
WHERE "Id" = <gameId>;
```
:::

See [/guide/features/king-of-the-hill](/guide/features/king-of-the-hill) for how these feed into scoring, and [/guide/features/scoring](/guide/features/scoring) for SLA scoring details. Unlike SLA (which scales by `sqrt(active teams)`), KotH hold points are flat per held tick — KotH is a zero-sum race for one marker, so field size doesn't dilute it.

---

## Per-challenge A&D settings

A&D challenge knobs live on the `GameChallenge` row and are authored in the `ad:` block of a challenge's YAML (alias mapping in `ChallengeYamlModel.AdSection`), or edited per challenge in the admin challenge form (`ChallengeUpdateModel`). They are only consulted when the challenge `type` is `AttackDefense`.

| YAML field (`ad:`) | DB field (`GameChallenge.*`) | Type | Default | Meaning |
| --- | --- | --- | --- | --- |
| `checkerImage` | `AdCheckerImage` | string? | null | Docker image for the per-challenge checker container. Run as a one-shot container with an enochecker3-style env contract (`GZCTF_ACTION=check`, `GZCTF_TARGET_IP`, `GZCTF_TARGET_PORT`, `GZCTF_FLAG`, `GZCTF_ROUND`, `GZCTF_TEAM_ID`, `GZCTF_CHALLENGE_ID`); the container exit code maps to the verdict (0=Ok, 1=Mumble, 2=Offline, other=InternalError). When omitted, the platform falls back to a TCP-reachability probe (`nc -z -w3`). |
| `allowEgress` | `AdAllowEgress` | bool | `true` | If true, team containers can reach the public internet. Set `false` to sandbox a service that should have no egress. |
| `allowSelfReset` | `AdAllowSelfReset` | bool | `true` | If true, teams can self-reset their own container to the baseline image, subject to the event-wide `AdResetCooldownMinutes` cooldown. Set `false` for fragile services that shouldn't be resettable. |

```yaml
# challenge.yml
name: notes-php
type: AttackDefense
category: Web
container:
  containerImage: registry.example.com/ad/notes-php:latest
  exposePort: 80
ad:
  checkerImage: registry.example.com/ad/notes-php-checker:latest
  allowEgress: true     # default
  allowSelfReset: true  # default
```

:::info
Tick length, flag lifetime, reset cooldown, snapshot-download, and the checker timing knobs (getflag jitter window + min grace period) are **event-wide** and live on the `Game` row / `.gzevent` manifest — they are deliberately not per-challenge, because rounds span the whole game. Set those in the game-level `ad:` block, not the challenge `ad:` block.
:::

For the full challenge YAML schema (containers, flags, hints, provide files) and the `.gzevent` event manifest, see [/guide/authoring/challenge-yaml](/guide/authoring/challenge-yaml) and [/guide/authoring/repo-bindings](/guide/authoring/repo-bindings).
```
