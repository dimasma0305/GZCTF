# Deploy with Docker

This page is a complete walkthrough of standing up a real event on the **Docker** container backend using the [`gzctf-platform-template`](https://github.com/TCP1P/gzctf-platform-template) repo. That template is now the supported way to run GZCTF: it ships a **published image** (no building from source), an interactive wizard that generates every secret for you, a Traefik front-end with automatic Let's Encrypt TLS, and all platform config in a single `appsettings.json`.

Docker is the simplest provider to operate: challenge instances spawn as host Docker containers, the A&D `/flag` is delivered as a read-only bind-mount, and you get precise `docker diff` plus post-game snapshot tarballs with no Kubernetes cluster to babysit.

If you are weighing Docker against Kubernetes, read [/guide/deployment/provider](/guide/deployment/provider) first. For the exhaustive list of configuration keys, see [/config/appsettings](/config/appsettings).

## Quick start

```bash
git clone https://github.com/TCP1P/gzctf-platform-template
cd gzctf-platform-template

make wizard       # interactive prompts → writes compose/.env + compose/appsettings.json
make setup        # creates the external 'traefik' + 'challenges' docker networks
make platform-up  # renders config if missing, then starts gzctf + db + cache + traefik
```

The wizard **prints the auto-generated admin password once, at the end** — copy it before you close the terminal. Then browse to `https://PUBLIC_ENTRY`, log in as user **`Admin`** with that password, and change it from the profile menu.

:::tip
There is no build step and no migration step. The image is pulled (`dimasmaualana/gzctf:develop`), and database migrations run **automatically** on first boot — the schema is created on launch, and later boots apply any new migrations before serving traffic.
:::

## Repository layout

Everything lives under two directories: `compose/` (the docker-compose stack) and `scripts/` (the wizard + config renderer). The `k8s/` directory is the Kubernetes alternative — see [Kubernetes](#kubernetes) below.

| Path | What it is |
| --- | --- |
| `compose/compose.yml` | The base stack: `gzctf`, `db`, `cache`, `wireguard`, `ssh-jump`. Always loaded. |
| `compose/compose.traefik.yml` | Overlay adding the `traefik` reverse proxy + Let's Encrypt TLS on ports 80/443. Loaded by the TLS targets. |
| `compose/compose.standalone.yml` | Overlay for **no-TLS / local** runs — exposes `gzctf` directly on host `:8080`. Loaded by the `*-no-traefik` targets. |
| `compose/appsettings.example.json` | The config template, with `{{.PublicEntry}}` / `{{.XorKey}}` / `{{.PostgresPassword}}` / `{{.AdSshInternalSecret}}` / `{{.AdSshPublicPort}}` placeholders. |
| `compose/appsettings.json` | The **rendered** config gzctf actually mounts. Generated from the example; `chmod 600`, gitignored. Never commit it. |
| `compose/.env.example` | Starter env file. |
| `compose/.env` | The **rendered** env: `PUBLIC_ENTRY`, `WORKSPACE`, `ACME_EMAIL`, and the auto-generated secrets. `chmod 600`, gitignored. Never commit it. |
| `Makefile` | All the operator targets (`make help` lists them). |

The `make` targets always run docker-compose from inside `compose/` with the right overlay combination, so you never compose the `-f` flags by hand.

## The wizard and config rendering

Two scripts own all the secret-handling. You normally only run the wizard once.

### `make wizard` (`scripts/wizard.sh`)

Interactive first-time setup. It prompts for:

- **`PUBLIC_ENTRY`** — the hostname participants type into their browser. **No scheme, no path** (e.g. `ctf.example.com`); the wizard rejects a value without a dot.
- **`ACME_EMAIL`** — where Let's Encrypt sends cert-expiry warnings.
- **SMTP relay** *(optional)* — host / port / sender / username / password, enabling email verification + password reset. Skippable; you can set it later under `/admin/settings` → Email.
- **Cloudflare Turnstile captcha** *(optional)* — site key + secret key, to slow down account-creation bots. Skippable; also settable later under `/admin/settings` → Captcha.

Everything else is **auto-generated**:

| Generated value | How | Lands in |
| --- | --- | --- |
| `WORKSPACE` | `gzctf-<8 random hex>` | `.env` (docker-compose project name + Traefik route suffix) |
| `XOR_KEY` | `openssl rand -hex 32` (256 bits) | `.env` |
| `ADMIN_PASSWORD` | `Aa1` + random hex | `.env` |
| `POSTGRES_PASSWORD` | `Aa1` + random hex | `.env` |

:::info
The `Aa1` prefix on the generated passwords is deliberate: ASP.NET Identity's default password policy requires an uppercase letter, a lowercase letter, and a digit. Raw hex is all-lowercase and would silently fail `UserManager.CreateAsync`, leaving you with **no `Admin` user at all**.
:::

The wizard writes `compose/.env` and `compose/appsettings.json` (rendering the latter from `appsettings.example.json` via `sed`), `chmod 600`s both, and **refuses to overwrite an existing `appsettings.json`** — delete it first if you really want to re-run.

### `make init-config` (`scripts/init-config.sh`)

This is the non-interactive renderer, and it runs **automatically** as a prerequisite of `make platform-up`. If `appsettings.json` already exists it does nothing (idempotent). If `.env` is missing it copies `.env.example` for you and stops so you can fill in `PUBLIC_ENTRY` + `ACME_EMAIL`. On the edit-`.env`-by-hand path it generates any still-missing secrets — including `AD_SSH_INTERNAL_SECRET` (`openssl rand -hex 32`, written to both `.env` for the `ssh-jump` sidecar and `appsettings.json`'s `Ad.Ssh.InternalSecret` for `gzctf`) — substitutes the `{{.AdSshInternalSecret}}` / `{{.AdSshPublicPort}}` placeholders, persists everything back to `.env`, and prints any freshly-generated admin password once.

### What `.env` holds vs. `appsettings.json`

- **`compose/.env`** holds the values docker-compose itself needs: `WORKSPACE` (project name), `PUBLIC_ENTRY` + `ACME_EMAIL` (Traefik routing + ACME), `POSTGRES_PASSWORD` (passed to the `db` container's `POSTGRES_PASSWORD`), `AD_SSH_INTERNAL_SECRET` (passed to the `ssh-jump` sidecar's `INTERNAL_SECRET`), and `ADMIN_PASSWORD` (passed to gzctf's first-boot seed). It is shell-style `KEY=VALUE`.
- **`compose/appsettings.json`** holds the platform's full runtime configuration — the same `POSTGRES_PASSWORD`, `XorKey`, and `AdSshInternalSecret` are substituted in here too, alongside `ContainerProvider`, `Ad`, `HoneypotConfig`, `ForwardedOptions`, and everything else (see [Configuration](#configuration)).

The secret that appears in **both** files (`POSTGRES_PASSWORD`, `AD_SSH_INTERNAL_SECRET`) is written from a single source so the two copies can't diverge.

:::danger
**Do not rotate `XorKey` or `POSTGRES_PASSWORD` after first boot.**

`XorKey` encrypts repo-binding tokens and registry passwords at rest — change it after data exists and every encrypted value in the DB becomes undecryptable. `POSTGRES_PASSWORD` is consumed by the `db` container only on its very first init; changing it later requires an `ALTER USER` inside the running database container plus a matching edit to `appsettings.json`'s `ConnectionStrings.Database`. Set both once, before the first `make platform-up`, and leave them alone.
:::

## The services

`compose.yml` defines five services; the `traefik` overlay adds a sixth. Two of them (`ssh-jump`, `wireguard`) are the A&D engine's networking sidecars; the rest are the platform, its datastores, and the front-end.

| Service | Image / Build | Role | Published ports |
| --- | --- | --- | --- |
| `gzctf` | `dimasmaualana/gzctf:develop` | The platform: web UI, API, A&D/KotH engine, container orchestrator, honeypot listeners | `2222`, `3306`, `5432`, `6379`, `11211`, `27017`, `9200` (honeypots) |
| `db` | `postgres:17` | Primary database | none (internal, on `app_net`) |
| `cache` | `redis:alpine` | Cache / scoreboard / signalling | none (internal, on `app_net`) |
| `wireguard` | builds `../wireguard` | Per-team WireGuard VPN endpoint into the challenge networks | `51820:51820/udp` |
| `ssh-jump` | builds `../ssh-jump` | SSH jump host into A&D challenge containers | `22022:22` (override `AD_SSH_PUBLIC_PORT`) |
| `traefik` | `traefik:latest` *(overlay)* | Reverse proxy + Let's Encrypt TLS termination | `80:80`, `443:443` |

:::info
Note that the `gzctf` service does **not** publish `8080` to the host in TLS mode — Traefik reaches it over the internal `traefik` network and terminates TLS for `Host(PUBLIC_ENTRY)` on the `websecure` entrypoint. The only host ports `gzctf` itself publishes are the **honeypot** listeners (see below). In standalone mode the overlay re-exposes `8080`.
:::

### The honeypot ports

The seven ports `gzctf` publishes — `2222` (ssh), `3306` (mysql), `5432` (postgres), `6379` (redis), `11211` (memcached), `27017` (mongo), `9200` (elastic) — are **honeypot listeners**, not real services. Each one matches an entry in `appsettings.json` → `HoneypotConfig.Ports`. A team that connects to one of these from inside a challenge network is logged, and the chain-detector flags repeated probing. Drop any port mapping in `compose.yml` that you don't want exposed, or disable the matching entry in `HoneypotConfig.Ports`.

### The `gzctf` service in detail

```yaml
volumes:
  - "gzctf-files:/app/files"
  - "./appsettings.json:/app/appsettings.json:ro"
  - "/var/run/docker.sock:/var/run/docker.sock"
  - "wg-config:/wg-config"
```

| Mount | Purpose |
| --- | --- |
| `gzctf-files` → `/app/files` | Uploaded attachments, avatars, writeups, generated artifacts. |
| `./appsettings.json` → `/app/appsettings.json:ro` | The rendered config, mounted read-only. |
| `/var/run/docker.sock` | The Docker provider talks to the host daemon to spawn, inspect (`docker diff`), snapshot, and tear down challenge containers. **Required** for the Docker backend. |
| `wg-config` → `/wg-config` | Shared with the `wireguard` sidecar (mounted there as `/config`). `AdWireGuardSyncService` reads the server keypair and writes `wg0.conf` here. |

`gzctf` joins three networks — `traefik` (front-end reach), `app_net` (control-plane reach to `db` + `cache`), and `challenges` (so `PlatformProxy` mode can reach challenge container IPs). It has a `:8080` healthcheck and a `deploy.resources` cap of 2 CPUs / 2 GB.

:::warning
Mounting `/var/run/docker.sock` gives the `gzctf` container full control of the host Docker daemon — effectively root on the host. This is inherent to the Docker provider (it needs the daemon to manage challenge containers). Run this host as a dedicated, isolated event box. If that boundary is unacceptable, use the Kubernetes provider instead — see [/guide/deployment/provider](/guide/deployment/provider).
:::

### The A&D sidecars

The A&D / KotH engine needs two pieces of network plumbing that the jeopardy platform does not.

- **`wireguard`** — the per-team VPN endpoint. It needs `cap_add: NET_ADMIN, SYS_MODULE`, `/dev/net/tun`, and the `src_valid_mark` / `ip_forward` sysctls to bring up the interface and route VPN traffic into the challenge networks. It shares the `wg-config` volume with `gzctf` (`/config` ↔ `/wg-config`): gzctf renders `wg0.conf`, the sidecar's inotify loop applies it. It sits on a **lonely** `wg-bootstrap` bridge with **no** interface on `app_net`, so a VPN client has no route to the control plane; `AdVpnTopology` (inside gzctf) attaches it to the challenge networks at runtime over the docker socket. The published `51820:51820/udp` must match `Ad.Vpn.ServerEndpoint` in `appsettings.json`.
- **`ssh-jump`** — the A&D SSH bastion. Players run `ssh <challenge-id>@PUBLIC_ENTRY -p 22022` to land a shell inside their **own** challenge container; the username is the challenge id and the registered SSH key identifies the team. The bastion has no shell accounts — it proxies stdio to `docker exec` in the resolved container through gzctf's internal endpoints, reaching gzctf at `GZCTF_URL=http://gzctf:8080` over `app_net` and authenticating with `INTERNAL_SECRET` (which must equal `Ad.Ssh.InternalSecret` — `init-config` writes both from the same `${AD_SSH_INTERNAL_SECRET}`).

:::tip
If you are running a **jeopardy-only** event with no A&D or KotH challenges, you can remove both sidecars: delete the `wireguard` and `ssh-jump` services, the `gzctf` `wg-config` volume mount, the `wg-bootstrap` network, and the `wg-config` volume.
:::

### Networks and volumes

| Network | Driver | Role |
| --- | --- | --- |
| `traefik` | external | Front-end: Traefik ↔ `gzctf`. Created by `make setup`. |
| `app_net` | bridge | Control plane: `gzctf` ↔ `db` ↔ `cache` ↔ `ssh-jump`. |
| `challenges` | external | Challenge container reach for `PlatformProxy`. Created by `make setup`; the same network challenge containers spawn onto. |
| `wg-bootstrap` | bridge | The lonely bridge the `wireguard` sidecar attaches to so its UDP port mapping works. |

| Volume | Backs |
| --- | --- |
| `gzctf-files` | `/app/files` — uploads + artifacts |
| `postgres-data` | the PostgreSQL data directory |
| `wg-config` | the shared WireGuard config (gzctf + the sidecar) |

`make setup` is what creates the two **external** networks (`traefik`, `challenges`) idempotently — they must exist before `make platform-up`, including in standalone mode, because the `gzctf` service attaches to both.

## Configuration

All platform settings live in `compose/appsettings.json`, rendered from `appsettings.example.json`. This is standard ASP.NET Core JSON config — nested objects, not docker-compose `__` env vars. The full key reference is at [/config/appsettings](/config/appsettings); the load-bearing blocks for a Docker deploy are below.

### `ContainerProvider`

```json
"ContainerProvider": {
  "Type": "Docker",
  "PortMappingType": "PlatformProxy",
  "EnableTrafficCapture": true,
  "PublicEntry": "PUBLIC_ENTRY",
  "DockerConfig": {
    "SwarmMode": false,
    "ChallengeNetwork": "challenges",
    "Uri": "unix:///var/run/docker.sock",
    "UserName": "",
    "Password": ""
  },
  "KubernetesConfig": {
    "Namespace": "gzctf-challenges",
    "ConfigPath": "kube-config.yaml",
    "AllowCIDR": [ "10.0.0.0/8" ],
    "DNS": [ "8.8.8.8", "223.5.5.5" ]
  }
}
```

| Key | Value here | Meaning |
| --- | --- | --- |
| `Type` | `Docker` | Use the host Docker daemon as the backend. Set to `Kubernetes` to use the cluster backend instead (then `KubernetesConfig` applies). |
| `PortMappingType` | `PlatformProxy` | gzctf proxies player traffic to challenge instances through the platform itself instead of publishing a host port per instance. |
| `EnableTrafficCapture` | `true` | Enable per-connection traffic capture for challenge instances. |
| `PublicEntry` | `PUBLIC_ENTRY` | The public hostname the platform hands out in emails + scoreboard links. Rendered from `.env`'s `PUBLIC_ENTRY`. |
| `DockerConfig.ChallengeNetwork` | `challenges` | Name of the platform-managed challenge bridge network — the same external `challenges` network the `gzctf` service joins. |
| `DockerConfig.Uri` | `unix:///var/run/docker.sock` | How the provider reaches the daemon. |

The `KubernetesConfig` block is present but inert under Docker.

### `Ad` (A&D / KotH engine)

```json
"Ad": {
  "Vpn": {
    "ConfigDir": "/wg-config",
    "ClientCidr": "10.13.37.0/24",
    "ServerEndpoint": "PUBLIC_ENTRY:51820",
    "Dns": "1.1.1.1"
  },
  "Ssh": {
    "InternalSecret": "<generated>",
    "PublicHost": "PUBLIC_ENTRY",
    "PublicPort": 22022
  }
}
```

| Key | Default | Notes |
| --- | --- | --- |
| `Vpn.ConfigDir` | `/wg-config` | Must match the `wireguard` sidecar's `/config` mount (both share the `wg-config` volume). |
| `Vpn.ClientCidr` | `10.13.37.0/24` | Address pool handed to VPN clients. |
| `Vpn.ServerEndpoint` | `PUBLIC_ENTRY:51820` | The host-reachable UDP endpoint clients dial; must match the published `51820/udp`. |
| `Ssh.InternalSecret` | *(generated)* | Shared secret authenticating `ssh-jump` to gzctf's internal lookup/exec endpoints. The platform **refuses the placeholder**, so without a real value `ssh <challenge-id>@PUBLIC_ENTRY -p 22022` stays disabled. `init-config` generates it and mirrors it into `.env`. |
| `Ssh.PublicHost` | `PUBLIC_ENTRY` | Host players SSH to. |
| `Ssh.PublicPort` | `22022` | Port players SSH to; must match the host side of the `ssh-jump` mapping (override with `AD_SSH_PUBLIC_PORT`). |

Other blocks in `appsettings.example.json` worth knowing: `HoneypotConfig` (the seven listener ports + chain detection), `FlagEgressConfig`, `CheatDetectionConfig`, `EmailConfig` / `CaptchaConfig` (the wizard fills these if you opt in), `RegistryConfig` (private image registry creds, also settable from `/admin/settings`), and `ForwardedOptions` — which is **already tuned for running behind Traefik** (`ForwardedHeaders: 5`, one proxy hop, trusted private nets), so you don't need to touch it for the default stack.

## Make-target reference

```bash
make help        # list every target with a one-line description
```

| Target | What it does |
| --- | --- |
| `make wizard` | Interactive first-time setup → writes `.env` + `appsettings.json`. |
| `make setup` | Create the external `traefik` + `challenges` networks (idempotent). |
| `make init-config` | Render `appsettings.json` from the example + `.env` (auto-runs on `platform-up`). |
| `make platform-up` | Start `gzctf` + `db` + `cache` + `traefik` (TLS). |
| `make platform-up-no-traefik` | Start `gzctf` + `db` + `cache` only, exposing `gzctf` on host `:8080`. |
| `make platform-down` | Stop everything; **keeps volumes**. |
| `make platform-restart` | `platform-down` then `platform-up`. |
| `make platform-clean` | Stop everything **and drop volumes** — data loss. |
| `make platform-logs` | Tail logs for all services. |
| `make gzctf-logs` / `db-logs` / `cache-logs` / `traefik-logs` | Tail one service. |
| `make traefik-restart` | Restart traefik only. |
| `make flush-cache` | `redis-cli FLUSHALL` on `cache` — rebuilds the scoreboard cache on next request. |
| `make pull` / `pull-no-traefik` / `pull-gzctf` | Pull the latest image(s) without recreating anything. |
| `make update` / `update-no-traefik` / `update-gzctf` | Pull, then recreate the changed container(s). |

:::warning
**Do not run `make platform-clean` on a live event.** It runs `docker compose down -v`, which deletes the `postgres-data`, `gzctf-files`, and `wg-config` volumes — wiping the database, uploaded files, and the WireGuard config. Use `make platform-down` to stop without data loss, and take a Postgres dump before any risky operation.
:::

## Updating the platform image

The stack runs a published image, so upgrades are a pull + recreate — no source checkout, no rebuild.

```bash
# Pull the new gzctf image and recreate just the gzctf container.
# traefik + db + cache keep running; only gzctf blips.
make update-gzctf
```

Migrations apply automatically on the new container's startup. `make update-gzctf` only touches `gzctf`. To refresh **every** image (including `traefik`/`postgres`/`redis`) and recreate any container whose digest changed, use `make update` (or `make update-no-traefik` in standalone mode). If you'd rather pull first and recreate later, `make pull-gzctf` / `make pull` download images without restarting anything.

## No-TLS / local runs

For a workstation or an internal box without a public hostname:

```bash
make platform-up-no-traefik   # gzctf on http://<host>:8080
```

This loads `compose.standalone.yml` instead of the Traefik overlay. The external `traefik` + `challenges` networks still need to exist (`make setup` creates them); the `traefik` network membership is harmless when no Traefik container is using it.

:::info
`appsettings.json` → `ContainerProvider.PublicEntry` was rendered from `.env`'s `PUBLIC_ENTRY` as an HTTPS hostname. For standalone use, edit it to e.g. `http://<host>:8080` so emails and scoreboard links resolve to the right place.
:::

## Importing example challenges

The companion [`TCP1PADTesting`](https://github.com/TCP1P/TCP1PADTesting) repo ships four ready-to-run challenges (two A&D, two KotH; OWASP web + heap pwn) you can import to populate a game and exercise the engine end to end. You import it via a **repo binding** — gzctf clones the repo itself and globs `.gzevent` recursively:

1. Log in as `Admin`, go to **Repo Bindings → Add**.
2. Set `RepoUrl` to `https://github.com/TCP1P/TCP1PADTesting`, leave the ref empty, set `IntervalSeconds` to `60`, and add **no token** (the repo is public).
3. **Scan now**, then watch the eight images build under **admin → Builds** (each challenge ships an auto-built service + checker, neither pinned).

Challenges import **hidden**; set the start/end times and unhide them in **admin → game → Info**. The A&D round settings on that Info page are covered in [/guide/deployment/provider](/guide/deployment/provider) and the appsettings reference.

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

## Kubernetes

The template also ships a `k8s/` directory for running the same platform on k3s (or any other distribution), swapping `ContainerProvider.Type` to `Kubernetes` so challenge instances spawn as pods in the `gzctf-challenges` namespace. Apply the manifests **in order**:

```bash
kubectl apply -f 00-namespace.yaml
kubectl apply -f 10-postgres.yaml
kubectl apply -f 20-redis.yaml
kubectl apply -f 30-gzctf-config.yaml
kubectl apply -f 40-gzctf.yaml
kubectl apply -f 50-ingress.yaml

kubectl -n gzctf rollout status deploy/gzctf
```

Secrets are supplied via the `gzctf-secrets` Secret (postgres password, `xor-key`, admin password — generate them with `openssl rand` exactly as the compose path does, and **don't rotate `xor-key` after first boot**). The platform's `appsettings.json` lives in a ConfigMap (`30-gzctf-config.yaml`), which also grants the gzctf ServiceAccount RBAC scoped to the `gzctf-challenges` namespace. See `k8s/README.md` in the template, and [/guide/deployment/provider](/guide/deployment/provider) for what the Kubernetes backend gives the A&D engine versus Docker.

## Where to go next

- [/guide/deployment/provider](/guide/deployment/provider) — Docker vs. Kubernetes: the trade-offs, and how to flip `ContainerProvider.Type` between them.
- [/config/appsettings](/config/appsettings) — the complete configuration key reference (every `ContainerProvider`, `Ad`, `Honeypot`, and `ForwardedOptions` key, with defaults).
