# Container Provider: Docker vs Kubernetes

The A&D and KotH engine runs on top of GZ::CTF's existing container abstraction (`IContainerManager` + `IContainerProvider`), so it works on **either** backend. But the two providers are not feature-equal: the Docker path delivers the full A&D toolkit (precise filesystem diffs, image snapshots, in-place container moves, the KotH leader-cooldown), while the Kubernetes path is a leaner, exec-light implementation built to survive on locked-down nodes. This page explains how to choose a backend, exactly what each one gives the A&D engine, and the operational limits you'll hit at scale.

## Choosing the provider

The backend is selected by a single config key, `ContainerProvider:Type`, which is a string enum with two values defined in `Configs.cs`:

```csharp
public enum ContainerProviderType
{
    Docker,      // default
    Kubernetes
}

public class ContainerProvider
{
    public ContainerProviderType Type { get; set; } = ContainerProviderType.Docker;
    // ...
    public KubernetesConfig? KubernetesConfig { get; set; }
    public DockerConfig? DockerConfig { get; set; }
}
```

If you set nothing, you get **Docker**. The wiring lives in `ContainerServiceExtension.cs`: at startup the `Type` switch registers exactly one provider/manager pair and never both — there is no runtime fallback from one to the other.

| `Type` | Provider registered | Manager registered | Image builder / exec / checker |
| --- | --- | --- | --- |
| `Docker` | `DockerProvider` | `DockerManager` | `DockerChallengeImageBuilder`, `DockerContainerExecChannel`, `AdCheckerExecutor` |
| `Kubernetes` | `KubernetesProvider` | `KubernetesManager` | `K8sChallengeImageBuilder`, `K8sContainerExecChannel`, `K8sAdCheckRunner` |

In this deployment the value is set as a docker-compose environment variable (double-underscore = config nesting):

```yaml
# docker-compose.yml
environment:
  - ContainerProvider__Type=Docker
  # Kubernetes-only keys are read only when Type=Kubernetes:
  - ContainerProvider__KubernetesConfig__KubeConfig=/app/kube-config.yaml
  - ContainerProvider__KubernetesConfig__Namespace=gzctf-challenges
  - ContainerProvider__KubernetesConfig__ImagePullPolicy=IfNotPresent
  - ContainerProvider__KubernetesConfig__AllowCidr__0=172.0.12.0/24
  - Ad__FlagPullBaseUrl=http://172.0.12.1:8080
```

See [/config/appsettings](/config/appsettings) for the full `ContainerProvider`, `DockerConfig`, and `KubernetesConfig` schemas.

:::warning Switching providers relaunches every challenge container
The provider is bound once, at process start, by DI. To switch you change `ContainerProvider:Type` and restart gzctf. The A&D reconcile loop (`AdContainerManager`, every 15 s) then sees the new backend with **no record of the old backend's containers**, so it launches a fresh container for every (team, challenge) and every KotH hill from the base image. Any in-flight game state that lived inside the containers — team patches, planted footholds, the `/koth/king` marker, accumulated filesystem changes — is wiped. Treat a provider switch as a hard reset of all live challenge instances; do not flip it mid-game.
:::

## What A&D gets on Docker

Docker is the reference backend and the one this deployment runs. The provider (`DockerProvider`) eagerly creates the challenge bridges at boot via `EnsureNetworkCreated()`.

### Networks ("per-team" bridges via Open / Isolated modes)

`DockerProvider` derives bridge names from `DockerConfig.ChallengeNetwork` (default prefix `gzctf`, set to `challenges` here) and one bridge per `NetworkMode`:

```text
{prefix}-open       bridge, ip_masquerade on   → can reach the internet
{prefix}-isolated   bridge, ip_masquerade off  → no NAT, cannot reach the internet
{prefix}-custom     attached only if it already exists
```

A&D and KotH containers land on the **open** or **isolated** bridge according to the challenge's `AdAllowEgress` flag (in `LaunchOneAsync` / `LaunchKothTargetAsync`):

```csharp
var networkMode = challenge.AdAllowEgress ? NetworkMode.Open : NetworkMode.Isolated;
```

The isolated bridge is created with `com.docker.network.bridge.enable_ip_masquerade=false` so isolated challenges have no NAT path off-host. Note that gzctf attaches *itself* to the isolated bridge with `GwPriority = -100` so Docker never promotes that no-NAT gateway to gzctf's own default route (which would silently break gzctf's outbound git clones, mirrors, and webhooks).

:::info "Per-team" containment is per-IP, not per-bridge
All of one mode's containers share a single bridge; teams are not each given a dedicated Docker network. True team→team isolation is enforced at L3 by `AdEgressIsolationService`, which installs `DOCKER-USER` rules keyed on an **ipset of the actual challenge-container IPs** read from the DB (chain `GZCTF_AD_ISO`, sets `gzctf_chal` / `gzctf_chal_koth`). That's covered in [Egress isolation](#egress-isolation) below.
:::

### `/flag` read-only bind mount

A&D flags rotate every tick, so the engine never bakes the flag into an env var (a baked `GZCTF_FLAG` would freeze at create time and go stale after the first rotation). Instead the flag is a **host-backed file bind-mounted read-only at `/flag`**.

`AdFlagMountService` keeps one host file per `(participationId, challengeId)` under `/app/ad-flags` (its in-container view), resolves the corresponding *host* path, warms up the file (Docker bind-mounts a missing source as an empty **directory**, which would break `/flag`), and hands the path to `DockerManager.GetCreateContainerParameters` as a read-only mount:

```csharp
Mounts = new List<Mount>
{
    new()
    {
        Type = "bind",
        Source = config.FlagBindSource,   // host path of the per-team flag file
        Target = config.FlagFilePath ?? "/flag",
        ReadOnly = true
    }
}
```

The read-only mount returns `EROFS` on write/unlink even to container-root (`CAP_DAC_OVERRIDE` does not bypass a mount-layer block, and remount/unmount needs `CAP_SYS_ADMIN`, dropped by default). gzctf rewrites the file **in place** each tick and the container sees the new flag through the shared inode. The in-container path is surfaced as `GZCTF_FLAG_FILE` so challenge code reads the live flag from there.

KotH hills get **no** flag mount — teams plant their own rotating token into the `/koth/king` marker, so there is no platform-planted flag to deliver.

### Precise live diff + snapshot tarballs

Because Docker exposes a layer-diff API, the "what did this team change" view is exact. `ComputeLiveChangesAsync` calls `docker diff` (`InspectChangesAsync`) against the baseline image, then:

- collapses ancestor directories to the changed **leaf** files (Docker reports every parent dir of a change), and
- filters runtime/churn noise via `IsNoiseChangePath` — the flag mount, `__pycache__`, `/tmp`, `/run`, `/var/log`, `/proc`, `/sys`, `/dev`, etc.

This gives add/modify/delete classification on the running container. At game end (when `Game.AdAllowSnapshotDownload` is set), `TrySnapshotAsync` additionally:

1. captures the `docker diff` into `SnapshotChanges`,
2. `docker commit`s the container to an image,
3. `docker save`s it, gzips it, and uploads the **tarball** to blob storage at `ad-snapshots/{gameId}/{participationId}-{challengeId}.tar.gz`,
4. deletes the local image (the tarball is the deliverable).

File-level inspection (`ReadCurrentFileBytesAsync`) reads bytes straight from the container filesystem via the daemon's tar archive API — no shell or coreutils needed in the image.

### In-place retain-restart and network moves

On Docker the engine can change a container's network **without destroying it**. When an operator toggles `AdAllowEgress` mid-game, `TryMoveContainerNetworkAsync` connects the container to the new bridge, disconnects the old one (connect-before-disconnect so it's never on zero networks), re-reads the new IP, and the team **keeps all its patches**. A full recreate happens only if the move fails.

The KotH **leader-cooldown** is also Docker-only: at each refresh boundary the recent leader's VPN `/32`s are dropped to the hill IP via `iptables` in the WireGuard sidecar's netns (per-hill chain `KOTH_CD_<challengeId>`).

## What A&D gets on Kubernetes

The K8s path (`KubernetesManager` / `KubernetesProvider`) is deliberately exec-light so it can run on managed/virtual nodes that forbid `exec`, `initContainers`, and `subPath`. It launches the same challenge images and the marker-based scoring works, but flag delivery, isolation, and inspection are implemented differently.

### Flag-pull sidecar → `/gzctf-flag/flag`

There is no bind mount. Instead K8s uses a **pull model**: a tiny `busybox:stable` sidecar (`gzctf-flag-writer`) polls a flag URL every 5 s and writes the flag into a shared `emptyDir`, which the challenge container mounts **read-only** at the flag directory.

`AdContainerManager.LaunchOneAsync` builds the pull URL from `Ad:FlagPullBaseUrl` and the per-pod token:

```csharp
flagFilePath = "/gzctf-flag/flag";
var baseUrl = config["Ad:FlagPullBaseUrl"]?.TrimEnd('/');
// ...
flagPullUrl =
    $"{baseUrl}/api/Game/{gameId}/Ad/PodFlag/{participationId}/{challengeId}/{podToken}";
```

`KubernetesManager` then renders the writer sidecar alongside the challenge:

```yaml
# challenge container mounts the shared volume read-only at the flag's *directory*
volumeMounts:
  - name: ad-flag
    mountPath: /gzctf-flag
    readOnly: true
---
# sidecar (gzctf-flag-writer) re-pulls every 5s into the shared emptyDir
containers:
  - name: gzctf-flag-writer
    image: busybox:stable
    command: ["sh", "-c", "while true; do wget -qO /flagdir/flag \"$GZCTF_FLAG_URL\"; sleep 5; done"]
    env:
      - name: GZCTF_FLAG_URL
        value: "http://<ip>:8080/api/Game/.../Ad/PodFlag/..."
    volumeMounts:
      - name: ad-flag
        mountPath: /flagdir
volumes:
  - name: ad-flag
    emptyDir: {}
```

There are no `initContainers` (virtual nodes don't support them) — the sidecar seeds the flag on its first poll, so the challenge may briefly see a missing file at startup. On real nodes the read-only mount protects the file; on virtual nodes that don't enforce `readOnly`, container-root *can* delete it, but the next poll (≤ 5 s) re-plants it.

:::danger `Ad:FlagPullBaseUrl` must be an IP, not a hostname
The K8s egress NetworkPolicy carves out the flag-pull endpoint as an `ipBlock` CIDR (`{host}/32`), so the value is only usable when its host parses as an IP. `KubernetesProvider` checks exactly this:

```csharp
if (Uri.TryCreate(configuration["Ad:FlagPullBaseUrl"], UriKind.Absolute, out var fp)
    && System.Net.IPAddress.TryParse(fp.Host, out _))
{
    _flagPullHost = fp.Host;
    _flagPullPort = fp.Port;
}
```

A DNS name leaves `_flagPullHost` null → **no flag-pull allow-rule** is emitted → isolated (and even open) pods can never receive their flag. The reconciler also logs a warning if `Ad:FlagPullBaseUrl` is unset. Use the reachable IP of the gzctf control plane (e.g. `http://172.0.12.1:8080`).
:::

### Egress-style challenges only; live-diff is an mtime heuristic

The K8s path is best suited to **egress-style** challenges (network services reachable over the pod network). Because there is no layer-diff API:

- **Live diff** (`ComputeLiveChangesAsync`, K8s branch) `exec`s `find / -xdev … -newer /proc/1 -type f` in the pod — an **mtime heuristic** that lists files modified since the container started. It has no add/modify/delete classification and **misses deletions**.
- **Snapshots are Docker-only.** `TrySnapshotAsync` returns null (logged) when no Docker provider is registered; only `SnapshotChanges` (the filtered file list) is captured at game end, not an image tarball.

### Retain-restart limitation

:::warning No in-place network move on Kubernetes
The non-destructive `TryMoveContainerNetworkAsync` is guarded by `dockerProvider is not null` — on K8s a network change (egress toggle) forces a full pod recreate, so the team loses its patches. Likewise the **KotH leader-cooldown is not applied on K8s**: the iptables throttle lives in the WireGuard sidecar's netns, which only the Docker provider can `exec` into. The hill still launches and marker-scoring still works, but the front-runner can immediately re-pwn a freshly-reset hill with no network block. `AdContainerManager` logs this once per game (`WarnKothK8sCooldownOnce`). KotH on K8s is best-effort until a NetworkPolicy parity is implemented.
:::

## Egress isolation

Both providers expose the same two network modes per challenge, driven by `AdAllowEgress`:

| Mode | `AdAllowEgress` | Reaches the internet? | Reaches other teams / private nets? |
| --- | --- | --- | --- |
| **Open** (`NetworkMode.Open`, default) | `true` | Yes | No — private + link-local denied |
| **Isolated** (`NetworkMode.Isolated`) | `false` | No | No |

The key point is that **open is not "wide open."** The open default still denies the private + link-local ranges defined once in `AdEgressBaseline.PrivateAndLinkLocal` and shared by both providers:

```csharp
public static readonly string[] PrivateAndLinkLocal =
    ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16"];
```

`169.254.0.0/16` is the important one — it blocks the cloud metadata service, so an RCE/SSRF challenge can't steal node IAM credentials. The other three are RFC1918 (cluster pod/service + private LAN).

- **Kubernetes** enforces this with two egress NetworkPolicies selected by the pod label `gzctf.gzti.me/NetworkMode`. The **open** policy allows `0.0.0.0/0` *except* the baseline + auto-detected node/control-plane addresses + any operator `AllowCidr`; the **isolated** policy denies all egress except the flag-pull endpoint and cluster DNS. `AllowCidr` *augments* the baseline — it can never re-open the private ranges. (A startup probe, `VerifyNetworkPolicy`, confirms the CNI actually enforces policy.)
- **Docker** has no egress-policy API, so `AdEgressIsolationService` installs equivalent `DOCKER-USER` rules (chain `GZCTF_AD_ISO`) keyed on an ipset of the live challenge-container IPs from the DB. It drops challenge→challenge (the lateral pivot) and challenge→`169.254/16, 10/8, 172.16/12, 192.168/16`, while leaving the checker, the WG sidecar, and container→internet allowed. It re-applies every 30 s (and on-demand right after a launch/move) so it survives a dockerd restart that flushes `DOCKER-USER`. Toggle with `DockerConfig.EnforceEgressIsolation` (default `true`).

The result is the same containment guarantee on both backends: a fully-popped challenge container cannot pivot team→team or reach cloud metadata, while flag delivery and the intended checker/VPN paths keep working.

## Capacity guidance

:::info These are observed operational ceilings, not configured limits
The numbers below come from stress-testing this deployment, not from a hard-coded cap in gzctf. Your mileage depends on subnet sizing, kernel, and host resources.
:::

On Docker, A&D scale is bounded by the shared challenge bridges:

| Ceiling | Observed value | Cause |
| --- | --- | --- |
| Usable IPs per egress bridge | **~254** per `/24` | One usable host address per IP in the bridge subnet (default `/24`). |
| Interfaces per Linux bridge | **hard ~1023** | Kernel limit — beyond it Docker returns "exchange full" on attach. |
| Host RAM headroom | ~3000+ containers | With the default per-container limits (`Memory`, `NanoCPUs`, `PidsLimit=512`); RAM is rarely the first wall. |

Because every A&D challenge spawns **one container per accepted team** (KotH spawns one shared hill per challenge), container count grows as `teams × A&D-challenges`. The first wall you hit is usually IP exhaustion on a `/24` egress bridge (~254), then the ~1023-interface bridge limit.

**To scale past these, shard the bridges**: split challenges across multiple challenge networks (a wider subnet per bridge raises the ~254 IP ceiling; multiple bridges raise the ~1023-interface ceiling), or move to Kubernetes where pods are spread across nodes and the per-bridge interface limit doesn't apply. Per-container resource limits (`CPUCount`, `MemoryLimit`, `StorageLimit`) are set per challenge — see [/guide/authoring/challenge-yaml](/guide/authoring/challenge-yaml). Provider-level network and capacity knobs (`ChallengeNetwork`, `AllowCidr`, `EnforceEgressIsolation`, `Ad:FlagPullBaseUrl`) are documented in [/config/appsettings](/config/appsettings); scoring behaviour that drives container churn (resets, SLA ticks) is covered in [/guide/features/scoring](/guide/features/scoring).
