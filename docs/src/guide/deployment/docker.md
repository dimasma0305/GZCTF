# Deploy with Docker

This page is a complete walkthrough of running a real event on the **Docker** container backend, based on the project's `docker-compose.yml`. Docker is the simplest provider to operate: challenge instances spawn as host Docker containers, the A&D `/flag` is delivered as a read-only bind-mount, and you get precise `docker diff` plus post-game snapshot tarballs with no Kubernetes cluster to babysit.

If you are weighing Docker against Kubernetes, read [/guide/deployment/provider](/guide/deployment/provider) first. For the exhaustive list of configuration keys, see [/config/appsettings](/config/appsettings).

## The services at a glance

The compose project is named `testing-gzctf` and defines five services. Two of them (`ssh-jump`, `wireguard`) are the A&D engine's networking sidecars; the rest are the platform and its datastores.

| Service | Image / Build | Role | Published ports |
| --- | --- | --- | --- |
| `gzctf` | builds `./src` via `GZCTF/Dockerfile.local` | The platform: web UI, API, A&D/KotH engine, container orchestrator | `8080:8080` |
| `ssh-jump` | builds `./ssh-jump` | SSH jump host into A&D challenge containers | `22022:22` (override `AD_SSH_PUBLIC_PORT`) |
| `wireguard` | builds `./wireguard` | WireGuard VPN endpoint into the challenge networks | `51820:51820/udp` |
| `postgres` | `postgres:16-alpine` | Primary database | none (internal) |
| `redis` | `redis:alpine` | Cache / signalling | none (internal) |

:::info
The compose file builds `gzctf` from source (`build.context: ./src`, `dockerfile: GZCTF/Dockerfile.local`) rather than pulling a published image. The same tree is the deploy worktree, so edit → build → up puts your change live. For production you can swap the `build:` block for an `image:` pin (see [Upgrades](#upgrades) below).
:::

## The `gzctf` service

This is the control plane. Everything else exists to support it.

### Ports

Only `8080:8080` is published. That is the web UI and API. The commented-out lines (`3306`, `5432`, `6379`, `11211`, `27017`, `9200`) are datastore ports you would only expose for debugging — leave them commented in production.

### Volumes

```yaml
volumes:
  - /var/run/docker.sock:/var/run/docker.sock
  - "gzctf-files:/app/files"
  - "gzctf-repos:/app/repos"
  - "wg-config:/wg-config"
  - "ad-flags:/app/ad-flags"
  - "./kube-config.k3d.yaml:/app/kube-config.yaml:ro"
```

| Mount | Purpose |
| --- | --- |
| `/var/run/docker.sock` | The Docker provider talks to the host Docker daemon to spawn, inspect (`docker diff`), snapshot, and tear down challenge containers. **Required** for the Docker backend. |
| `gzctf-files` → `/app/files` | Uploaded attachments, avatars, writeups, and generated artifacts. |
| `gzctf-repos` → `/app/repos` | Git repositories used by the platform. |
| `wg-config` → `/wg-config` | Shared with the `wireguard` sidecar (mounted there as `/config`). `AdWireGuardSyncService` reads `server.pub` / `server.key` and writes `wg0.conf` here. |
| `ad-flags` → `/app/ad-flags` | Host-backed A&D flag files. gzctf auto-derives this volume's host path and bind-mounts each flag **read-only** into the matching team container so container-root cannot delete or tamper with `/flag`. Docker-provider only. |
| `./kube-config.k3d.yaml` → `/app/kube-config.yaml:ro` | Kubeconfig, used **only** if you flip the provider to Kubernetes. Harmless under Docker. |

:::warning
Mounting `/var/run/docker.sock` gives the `gzctf` container full control of the host Docker daemon — effectively root on the host. This is inherent to the Docker provider (it needs the daemon to manage challenge containers). Run this host as a dedicated, isolated event box. If that boundary is unacceptable, use the Kubernetes provider instead — see [/guide/deployment/provider](/guide/deployment/provider).
:::

### Networking

The `gzctf` service joins two networks:

```yaml
networks:
  - default
  - k3d
```

- `default` carries control-plane traffic to `postgres` and `redis` (which live on the same default compose network).
- `k3d` (external, named `k3d-gzctf`) lets gzctf reach the Kubernetes API server when running the K8s backend. Under Docker it is unused but joining it is harmless.

Notably, **the challenge networks are not declared here.** The Docker provider's `EnsureNetworkCreated` calls `AttachSelfToNetwork` on startup, so gzctf auto-joins the platform-managed `challenges-open` and `challenges-isolated` bridges right after the provider initializes. Compose only needs to give it the default network for control-plane connectivity.

## Core configuration (env vars)

ASP.NET Core reads configuration from environment variables, mapping the `__` (double-underscore) separator onto nested config sections. So `ConnectionStrings__Database` populates `ConnectionStrings:Database` in the app's configuration, equivalent to a key in `appsettings.json`. The full key reference lives at [/config/appsettings](/config/appsettings); the load-bearing ones for a Docker deploy are below.

### Connection strings

```yaml
- ConnectionStrings__Database=Host=postgres;Database=gzctf;Username=gzctf;Password=gzctf
- ConnectionStrings__Redis=redis:6379,password=gzctf
```

- **Database** is a Npgsql/PostgreSQL connection string. `Host=postgres` is the compose service name. The matching `postgres` service is seeded with `POSTGRES_DB=gzctf`, `POSTGRES_USER=gzctf`, `POSTGRES_PASSWORD=gzctf` — keep all three in sync if you change them.
- **Redis** is a StackExchange.Redis connection string (`host:port,option=value`). `password=gzctf` must match the `redis-server --requirepass gzctf` argument on the `redis` service.

:::danger
`gzctf` / `gzctf` / `gzctf-xor-key` are development defaults. Before a real event, change the Postgres password, the Redis password, and the `XorKey` together. Treat them as secrets.
:::

### XorKey

```yaml
- XorKey=gzctf-xor-key
```

`XorKey` is the secret gzctf uses to XOR-encrypt sensitive stored values (e.g. registered container/registry credentials). It is not a per-record key — if you change it after data exists, previously encrypted values can no longer be decrypted. Set it once, before first launch, to a strong random value.

### ContainerProvider

This block selects and tunes the container backend.

```yaml
- ContainerProvider__Type=Docker
- ContainerProvider__DockerConfig__ChallengeNetwork=challenges
- ContainerProvider__PortMappingType=PlatformProxy
- ContainerProvider__EnableTrafficCapture=true
```

| Key | Value here | Meaning |
| --- | --- | --- |
| `ContainerProvider__Type` | `Docker` | Use the host Docker daemon as the backend. Set to `Kubernetes` to use the k3d backend instead (then `KubernetesConfig` + `Ad__FlagPullBaseUrl` apply). |
| `ContainerProvider__DockerConfig__ChallengeNetwork` | `challenges` | Base name for the platform-managed challenge bridge networks (`challenges-open` / `challenges-isolated`). |
| `ContainerProvider__PortMappingType` | `PlatformProxy` | gzctf proxies player traffic to challenge instances through the platform itself instead of publishing a host port per instance. |
| `ContainerProvider__EnableTrafficCapture` | `true` | Enable per-connection traffic capture for challenge instances. |

The Kubernetes-only keys are present but inert under Docker:

```yaml
- ContainerProvider__KubernetesConfig__KubeConfig=/app/kube-config.yaml
- ContainerProvider__KubernetesConfig__Namespace=gzctf-challenges
- ContainerProvider__KubernetesConfig__ImagePullPolicy=IfNotPresent
- ContainerProvider__KubernetesConfig__AllowCidr__0=172.0.12.0/24
- Ad__FlagPullBaseUrl=http://172.0.12.1:8080
```

:::tip
**PublicEntry** — when you publish challenge instances by exposing host ports (rather than `PlatformProxy`), set `ContainerProvider__PublicEntry` to the host/IP players should connect to so the platform hands out reachable `host:port` addresses. With `PortMappingType=PlatformProxy` as configured here, traffic goes through the platform proxy and `PublicEntry` is not the mechanism in play. See [/config/appsettings](/config/appsettings).
:::

### Forwarded headers

Because gzctf sits behind a proxy, configure trust for forwarded headers so it reads the real client IP:

```yaml
- ForwardedOptions__ForwardedHeaders=1
- ForwardedOptions__ForwardLimit=1
- ForwardedOptions__TrustedNetworks__0=172.0.4.0/24
```

`ForwardedHeaders=1` enables `X-Forwarded-For`; `ForwardLimit=1` trusts one proxy hop; `TrustedNetworks__0` whitelists the proxy subnet. Adjust the CIDR to your actual front-end network.

## A&D engine wiring

The A&D / KotH engine needs two pieces of network plumbing that the jeopardy platform does not: an SSH jump host and a WireGuard endpoint. Both are sidecar services that share secrets and config with `gzctf`.

### A&D env on `gzctf`

```yaml
# WireGuard
- Ad__Vpn__ConfigDir=/wg-config
- Ad__Vpn__ClientCidr=10.13.37.0/24
- Ad__Vpn__ServerEndpoint=${AD_VPN_SERVER_ENDPOINT:-1pc.tf:51820}
# SSH jump
- Ad__Ssh__InternalSecret=${AD_SSH_INTERNAL_SECRET:-dev-only-rotate-me-before-prod}
- Ad__Ssh__PublicHost=${AD_SSH_PUBLIC_HOST:-1pc.tf}
- Ad__Ssh__PublicPort=${AD_SSH_PUBLIC_PORT:-22022}
# Checker
- Ad__Checker__MaxParallel=${AD_CHECKER_MAX_PARALLEL:-10}
- Ad__Checker__TimeoutSeconds=${AD_CHECKER_TIMEOUT_SECONDS:-30}
```

| Key | Default | Notes |
| --- | --- | --- |
| `Ad__Vpn__ConfigDir` | `/wg-config` | Must match the `wireguard` sidecar's `/config` mount (both share the `wg-config` volume). `AdWireGuardSyncService` reads `server.pub`/`server.key` and writes `wg0.conf` here. |
| `Ad__Vpn__ClientCidr` | `10.13.37.0/24` | Address pool handed to VPN clients. |
| `Ad__Vpn__ServerEndpoint` | `1pc.tf:51820` | The host-reachable UDP endpoint clients dial. **Override per host** via `AD_VPN_SERVER_ENDPOINT` — set it to your public IP/hostname + the published UDP port. |
| `Ad__Ssh__InternalSecret` | `dev-only-rotate-me-before-prod` | Shared secret authenticating `ssh-jump` to gzctf's internal lookup/exec endpoints. The `ssh-jump` service must use the **same** value. |
| `Ad__Ssh__PublicHost` | `1pc.tf` | Host players SSH to. |
| `Ad__Ssh__PublicPort` | `22022` | Port players SSH to; must match the host side of the `ssh-jump` port mapping. |
| `Ad__Checker__MaxParallel` | `10` | Concurrent checker-container runs. Sized for ~50 teams × ~5 challenges; raise it if SLA badges lag the round. |
| `Ad__Checker__TimeoutSeconds` | `30` | Hard timeout per checker run. |

:::danger
Generate a real `AD_SSH_INTERNAL_SECRET` before going live: `openssl rand -hex 32`. The default literally says `dev-only-rotate-me-before-prod`. This secret is the only thing standing between the `ssh-jump` box and gzctf's internal exec endpoints — those services share a compose network with postgres/redis.
:::

### `ssh-jump` sidecar

```yaml
ssh-jump:
  build:
    context: ./ssh-jump
  environment:
    - GZCTF_URL=http://gzctf:8080
    - INTERNAL_SECRET=${AD_SSH_INTERNAL_SECRET:-dev-only-rotate-me-before-prod}
  ports:
    - "${AD_SSH_PUBLIC_PORT:-22022}:22"
  depends_on:
    - gzctf
  networks:
    - default
  restart: unless-stopped
```

`ssh-jump` is an SSH server whose `AuthorizedKeysCommand` and WebSocket relay call gzctf's control plane (`GZCTF_URL=http://gzctf:8080`, using the internal Docker DNS name so traffic stays on the compose network). `INTERNAL_SECRET` must equal `Ad__Ssh__InternalSecret` on `gzctf`. Players connect with:

```bash
ssh <challenge-id>@<your-host> -p 22022
```

where `<challenge-id>` is the routing username and `22022` is `AD_SSH_PUBLIC_PORT`.

### `wireguard` sidecar

```yaml
wireguard:
  build:
    context: ./wireguard
  cap_add:
    - NET_ADMIN
    - SYS_MODULE
  sysctls:
    net.ipv4.conf.all.src_valid_mark: 1
    net.ipv4.ip_forward: 1
  devices:
    - /dev/net/tun:/dev/net/tun
  ports:
    - "51820:51820/udp"
  volumes:
    - "wg-config:/config"
  networks:
    - wg-bootstrap
  restart: unless-stopped
```

Why each piece exists:

- `cap_add: NET_ADMIN, SYS_MODULE` and `devices: /dev/net/tun` are needed to bring up the WireGuard interface and program routing inside the container.
- `sysctls` enable source-valid-mark and IPv4 forwarding so VPN traffic can be routed into the challenge networks.
- `wg-config` → `/config` is the **same** volume gzctf mounts at `/wg-config`. gzctf writes `wg0.conf` and the sidecar serves it.
- The sidecar sits **only** on the dedicated `wg-bootstrap` network — intentionally lonely. It deliberately has **no** interface on the gzctf `default` network (where postgres/redis live), so even with a wide-open iptables FORWARD chain there is no route from a VPN client into the control plane. At runtime, `AdVpnTopology` (in gzctf) inspects and attaches the sidecar to the platform's challenge networks — so adding or renaming a challenge network needs no compose change.

:::info
The published UDP port (`51820:51820/udp`) must line up with the port in `Ad__Vpn__ServerEndpoint`. If you change one, change the other.
:::

## Datastores

```yaml
postgres:
  image: postgres:16-alpine
  environment:
    - POSTGRES_DB=gzctf
    - POSTGRES_USER=gzctf
    - POSTGRES_PASSWORD=gzctf
  volumes:
    - postgres_data:/var/lib/postgresql/data

redis:
  image: redis:alpine
  command: redis-server --requirepass gzctf
  volumes:
    - redis_data:/data
```

Both persist to named volumes (`postgres_data`, `redis_data`). The `gzctf` service `depends_on` both, so compose starts them first. Their credentials must match the corresponding `ConnectionStrings__*` values on `gzctf`.

## Volumes and networks summary

```yaml
volumes:
  postgres_data:
  redis_data:
  gzctf-files:
  gzctf-repos:
  wg-config:
  ad-flags:

networks:
  wg-bootstrap:
    driver: bridge
  k3d:
    external: true
    name: k3d-gzctf
```

The `k3d` network is `external: true` — it is created by `k3d cluster create`, not by this compose file. Under the Docker provider you can ignore it; under Kubernetes it must already exist (`name: k3d-gzctf`).

## Bring-up

From the repository root (where `docker-compose.yml` lives):

```bash
# Build the gzctf image and start everything in the background
docker compose up -d --build
```

Database migrations run **automatically** on startup — there is no separate migration step. The first boot creates the schema; later boots apply any new migrations before serving traffic.

Once the stack is up:

- Web UI / API: `http://<host>:8080`
- SSH jump: `<host>:22022`
- WireGuard: UDP `<host>:51820`

:::tip
The very first registered user becomes the platform administrator. Register immediately after first boot, before exposing the host, to claim the admin account.
:::

## Viewing logs

```bash
# Follow the platform logs
docker compose logs -f gzctf

# A specific sidecar
docker compose logs -f ssh-jump
docker compose logs -f wireguard

# Everything, last 200 lines, following
docker compose logs -f --tail=200
```

If A&D containers misbehave, the platform logs are where the provider reports container spawn / `docker diff` / snapshot activity, and where `AdWireGuardSyncService` / `AdVpnTopology` log their attach steps.

## Upgrades

The shipped compose builds from source. The upgrade flow is therefore: get the new code, rebuild, restart. Migrations apply automatically on the new container's startup.

```bash
# Pull the latest source for this fork
git pull

# Rebuild the gzctf image and recreate the container
docker compose up -d --build gzctf
```

If you instead run a **pinned published image** (recommended for production — replace the `build:` block with an `image:` line), the flow is the conventional pull + restart:

```bash
docker compose pull gzctf
docker compose up -d gzctf
```

:::warning
Named volumes (`postgres_data`, `redis_data`, `gzctf-files`, `gzctf-repos`, `wg-config`, `ad-flags`) survive `docker compose up`/`down`. Do **not** run `docker compose down -v` on a live event — `-v` deletes the volumes, wiping the database, uploaded files, the WireGuard config, and the A&D flag store. Take a Postgres dump before any risky operation.
:::

## Host tuning for many challenge containers

A&D spins up one challenge container per team per challenge, plus checker containers. On a large event that is hundreds of containers on one host. The default kernel limits for inotify watches/instances and open file handles are tuned for a workstation, not a container farm, and you will hit `too many open files` or inotify-exhaustion failures (containers failing to start, file-watch features going silent) long before you run out of RAM or CPU.

Raise them on the **host** (these affect the daemon and all containers):

```bash
# Inspect current values
sysctl fs.inotify.max_user_instances fs.inotify.max_user_watches
sysctl fs.file-max
```

Persist higher limits in `/etc/sysctl.d/99-gzctf.conf`:

```text
# inotify: each container/runtime consumes instances + watches
fs.inotify.max_user_instances = 8192
fs.inotify.max_user_watches   = 1048576
# system-wide open file handles
fs.file-max = 2097152
```

Apply without rebooting:

```bash
sudo sysctl --system
```

Also raise the open-files limit for the Docker daemon's process (the `nofile` ulimit). With systemd-managed Docker:

```bash
# /etc/systemd/system/docker.service.d/override.conf
[Service]
LimitNOFILE=1048576
```

```bash
sudo systemctl daemon-reload
sudo systemctl restart docker
```

:::info
**Network capacity ceilings.** A single Docker bridge tops out around **1023 containers** before the kernel returns "address space exhausted", and an egress `/24` gives roughly **254** usable IPs. Host RAM typically allows a few thousand containers before either of those matters — so for very large fields, scale by sharding across additional challenge bridges or moving to macvlan, not by adding RAM. Plan bridge/subnet count against `teams × challenges`.
:::

:::tip
After raising limits, sanity-check while a game is running:

```bash
# How many challenge containers are live right now
docker ps --format '{{.Names}}' | wc -l

# inotify instances in use across the host
find /proc/*/fd -lname 'anon_inode:inotify' 2>/dev/null | wc -l
```
:::

## Where to go next

- [/guide/deployment/provider](/guide/deployment/provider) — Docker vs. Kubernetes: the trade-offs, and how to flip `ContainerProvider__Type` between them.
- [/config/appsettings](/config/appsettings) — the complete configuration key reference (every `ContainerProvider`, `Ad`, `Honeypot`, and `ForwardedOptions` key, with defaults).
