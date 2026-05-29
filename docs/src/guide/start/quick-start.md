# Quick Start

This guide takes you from a clean Docker host to a running platform with your first Attack & Defense (A&D) or King of the Hill (KotH) game. It is written against the `docker-compose.yml` shipped in this fork, so every service, port, and default below is the one you actually get when you bring the stack up.

If you only want a vanilla jeopardy CTF, you can ignore the `ssh-jump` and `wireguard` services entirely — they exist solely to give A&D/KotH teams SSH and VPN access to their containers. Everything else (the `gzctf` app, Postgres, Redis) is the standard GZ::CTF deployment.

For exhaustive deployment options see [/guide/deployment/docker](/guide/deployment/docker); for every configuration key see [/config/appsettings](/config/appsettings).

## Prerequisites

- **Docker Engine** with the **Compose v2 plugin** (`docker compose ...`, not the legacy `docker-compose` binary).
- The Docker daemon socket at `/var/run/docker.sock` — the `gzctf` container bind-mounts it so it can spawn and manage challenge containers directly on the host.
- A host kernel with the `tun` module available (`/dev/net/tun`) **if** you intend to run the WireGuard sidecar for A&D VPN access.
- Inbound access to the ports you expose: `8080/tcp` (web UI), and for A&D `51820/udp` (WireGuard) and `22022/tcp` (SSH jump, configurable).

:::info
This compose file sets `ASPNETCORE_ENVIRONMENT=Development` and builds `gzctf` from local source (`./src`, `Dockerfile.local`). It is a development/self-host configuration, not a hardened production image. Read the warnings below about secrets and the auto-created admin before exposing it publicly.
:::

## The services

The compose project is named **`testing-gzctf`** (the `name:` at the top of the file). All five services below come up together.

| Service | Image / build | Purpose |
| --- | --- | --- |
| `gzctf` | built from `./src` (`GZCTF/Dockerfile.local`) | The platform itself: web UI, API, scoreboard, and the container orchestrator. Listens on `8080`. |
| `postgres` | `postgres:16-alpine` | Primary datastore (users, teams, games, challenges, submissions). |
| `redis` | `redis:alpine` | Cache and the SignalR backplane for real-time scoreboard/notifications. |
| `ssh-jump` | built from `./ssh-jump` | **A&D only.** SSH bastion that lets a team SSH directly into its own challenge container. |
| `wireguard` | built from `./wireguard` | **A&D only.** WireGuard VPN endpoint so teams can reach their containers over a private network. |

### gzctf — the application

This is the core. A few things worth knowing about how it is wired in this file:

- **Web UI / API** is published on host port `8080` (`8080:8080`).
- **Database / cache** are reached over the compose `default` network using the DNS names `postgres` and `redis`. Credentials are passed via `ConnectionStrings__Database` and `ConnectionStrings__Redis` (both `gzctf`/`gzctf` here).
- **Container backend** is selected by `ContainerProvider__Type`, set to `Docker`. In Docker mode, challenge instances spawn as host Docker containers on the platform-managed `challenges-{open,isolated}` bridge networks, and the planted A&D flag is delivered through the read-only `ad-flags` bind-mount rather than a pull sidecar. Set `ContainerProvider__Type=Kubernetes` to switch to the k3d backend instead (at which point `KubernetesConfig` and `Ad__FlagPullBaseUrl` take effect).
- **Docker socket** is mounted (`/var/run/docker.sock`) so the app can manage those challenge containers.
- **A&D round / VPN / SSH settings** are passed under the `Ad__*` keys (VPN CIDR `10.13.37.0/24`, SSH internal secret, checker parallelism, etc.). See [/config/appsettings](/config/appsettings) for the full list.

:::warning
Two secrets in this file have placeholder defaults you must change before any real deployment:

- `Ad__Ssh__InternalSecret` (also consumed by `ssh-jump` as `INTERNAL_SECRET`) defaults to `dev-only-rotate-me-before-prod`. Generate a strong value with `openssl rand -hex 32` and set it via the `AD_SSH_INTERNAL_SECRET` environment variable so both services share it.
- The Postgres and Redis passwords are both literally `gzctf`.
:::

### postgres and redis

Plain backing stores. Their data persists in the named volumes `postgres_data` and `redis_data`. Neither publishes a host port by default (the commented-out `5432`/`6379` lines under `gzctf` are there if you want to expose them for debugging).

### ssh-jump — A&D SSH bastion

A small sidecar that fronts SSH access to challenge containers. It talks back to the app over the internal compose network at `GZCTF_URL=http://gzctf:8080`, authenticating every lookup/exec call with the shared `INTERNAL_SECRET`. It publishes host port **`22022`** (override with `AD_SSH_PUBLIC_PORT`), mapped to `22` inside. Players connect like so:

```bash
ssh <challenge-id>@<host> -p 22022
```

The `Ad__Ssh__PublicHost` and `Ad__Ssh__PublicPort` values on the `gzctf` service are what the UI shows players as the connection target, so keep them in sync with where `ssh-jump` is actually reachable.

### wireguard — A&D VPN endpoint

Gives A&D teams a private route to their containers. It needs elevated networking (`NET_ADMIN`, `SYS_MODULE`, `/dev/net/tun`, and the `src_valid_mark` / `ip_forward` sysctls) and publishes **`51820/udp`**. It shares the `wg-config` volume with `gzctf`: the app's `AdWireGuardSyncService` reads `server.pub`/`server.key` and writes `wg0.conf` into `Ad__Vpn__ConfigDir=/wg-config`, which is the sidecar's `/config`.

By design the sidecar sits on its own lonely `wg-bootstrap` network and is *not* attached to the `default` network where Postgres and Redis live — the app attaches it to the challenge networks at runtime instead, so a VPN client can never bridge into the control plane.

:::warning
Set `AD_VPN_SERVER_ENDPOINT` to a host-reachable UDP endpoint that VPN clients can actually reach from outside (public IP or hostname + the published UDP port). The default is `1pc.tf:51820`; if you deploy elsewhere and forget to override it, generated client configs will point at the wrong host.
:::

## Bring it up

From the directory containing `docker-compose.yml`:

```bash
docker compose up -d
```

The `gzctf` image builds from source on first run, so the initial `up` takes a while. Watch progress with:

```bash
docker compose ps
docker compose logs -f gzctf
```

Once `gzctf` reports it is listening, open the web UI:

```text
http://<your-host>:8080
```

## Create the first admin account

Because this compose file runs with `ASPNETCORE_ENVIRONMENT=Development`, the platform bootstraps an admin for you on startup. At first launch it creates a user named **`Admin`** with the development password **`Admin@2022`**. Log in with those credentials immediately.

Additionally, in Development any account that *registers* through the normal sign-up form is granted the `Admin` role automatically (see `AccountController.Register`).

:::danger
`Admin@2022` is a well-known default and, in Development, **every** newly registered user becomes an admin. Do not expose this configuration to the internet as-is. For a production deployment, run with the Production environment and provide an `ADMIN_PASSWORD` configuration value: when set, the platform creates the `Admin` user with that password and does **not** auto-promote registrants. See [/config/appsettings](/config/appsettings).
:::

After logging in, change the admin password from the account settings, then head to the admin area to set up your game.

## Create a game

In the admin area, create a new game and fill in its basic info (title, start/end time, description). This is the standard GZ::CTF game-creation flow; nothing A&D-specific is required yet to get a jeopardy game running.

When you are ready to add challenges, open the game's challenge list and create a challenge. The challenge editor lets you pick the **challenge type**. Alongside the classic jeopardy types (Static/Dynamic Attachment, Static/Dynamic Container), this fork adds two A&D-engine types:

| Type | What it is |
| --- | --- |
| `AttackDefense` | Classic Attack & Defense: every team gets its own instance of the same vulnerable service; they patch their own and exploit others' to steal round flags. |
| `KingOfTheHill` | A single shared/contested target where teams compete for control each round. |

Both A&D-engine types share the same editor treatment in the UI: they run as managed containers and skip the per-flag configuration that jeopardy challenges use (the flags are issued and rotated by the A&D engine each round, not entered by hand). When you select `AttackDefense` or `KingOfTheHill`, the editor surfaces the container image and the relevant A&D fields and hides the manual flag inputs.

:::tip
For the full schema of an A&D/KotH challenge — image, checker, exposed ports, network mode — see [/guide/authoring/challenge-yaml](/guide/authoring/challenge-yaml).
:::

## A&D round settings live on the game's Info page

The settings that govern the *rhythm* of an A&D/KotH event are **per-game**, not per-challenge, and they live in **admin → game → Info** (the `Info` tab of the game editor). The key fields and their defaults, straight from the editor:

| Field | Default | Range | Meaning |
| --- | --- | --- | --- |
| A&D tick (seconds) | `60` | 30–600 | Length of one round/tick. Flags rotate and SLA checks run on this cadence. |
| Flag lifetime (ticks) | `5` | 1–50 | How many ticks a planted flag stays valid for submission before it expires. |
| Reset cooldown (minutes) | `5` | 0–60 | Minimum time a team must wait between resetting its own A&D instance. |
| Snapshot retention (days) | empty = forever | 1–3650 | How long post-game container snapshots are kept; leave blank to keep them indefinitely. |

There is also a *get-flag window fraction* and related fields below these in the same section. Set the tick length and flag lifetime here **before** the game starts — they define how the whole event is scored.

:::tip
Tick cadence and flag lifetime drive how points accrue. To understand how round flags, SLA, and stolen-flag points turn into scoreboard standing, read [/guide/features/scoring](/guide/features/scoring).
:::

## Where to go next

- [/guide/deployment/docker](/guide/deployment/docker) — full deployment detail: production environment, object storage, reverse proxy, the Kubernetes container backend, and scaling.
- [/config/appsettings](/config/appsettings) — every configuration key, including all `Ad__*`, `ContainerProvider__*`, and `ADMIN_PASSWORD`.
- [/guide/authoring/challenge-yaml](/guide/authoring/challenge-yaml) — authoring A&D/KotH and jeopardy challenges.
- [/guide/features/scoring](/guide/features/scoring) — how rounds, SLA, and flag captures are scored.
