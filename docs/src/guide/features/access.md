# Team Access: VPN & SSH

> This page documents a feature exclusive to this fork of GZ::CTF. Upstream GZ::CTF has no Attack & Defense network plane, no per-team WireGuard provisioning, and no SSH jump host.

In Attack & Defense (and King of the Hill), each team runs a live copy of every vulnerable service. To play, a team must be able to reach **its own** containers (to patch, inspect, and defend them) and **every opponent's** containers (to land exploits). Those containers live on private, platform-managed Docker networks that are not routable from the public internet. This fork bridges players onto that private plane two ways:

- **WireGuard VPN** — a per-user tunnel that puts your machine directly on the challenge subnets, so exploit scripts can dial `ip:port` as if they were on the wire.
- **SSH jump host** — a keyed bastion (`ssh-jump`) that drops you into a shell **inside your own** challenge container, for patching and inspection, without exposing Docker to anyone.

Both are surfaced to players through the per-game **Toolkit** sidebar, and both are revoked the instant a member leaves or is kicked from a team.

See also: [/guide/features/attack-defense](/guide/features/attack-defense) and [/guide/features/king-of-the-hill](/guide/features/king-of-the-hill).

---

## WireGuard VPN

### How it fits together

Three pieces cooperate:

| Piece | Role |
| --- | --- |
| `wireguard` sidecar (compose service) | Holds the kernel WireGuard interface. Generates the server keypair on first boot, watches `/config` with an inotify loop, and runs `wg syncconf` on every change so peer add/remove applies with no restart. |
| `AdWireGuardSyncService` (in gzctf) | Background reconciler. Every **15 s** it renders `wg0.conf` from the `AdVpnPeer` table into the shared `wg-config` volume. |
| `AdVpnTopology` (in gzctf) | Discovers the live challenge Docker subnets and idempotently attaches the sidecar to them, so VPN clients have a kernel route to challenge IPs. |

The sidecar deliberately sits on its own lonely `wg-bootstrap` network and is **never** attached to the gzctf control-plane network (postgres/redis). `AdVpnTopology` refuses to attach it to any network whose name contains `default`, `postgres`, `redis`, or `control`, or whose subnet is non-RFC1918 / loopback / link-local. There is no route from a VPN client into the control plane even if `iptables FORWARD` were wide open.

### Addressing & topology

The VPN subnet defaults to **`10.13.37.0/24`** (`Ad__Vpn__ClientCidr`). Within it:

- `.0` is the network address (skipped).
- `.1` is the WireGuard server itself (`ResolveServerIp` ORs the low bit of the host octet).
- `.2 … .254` are handed out to peers, one `/32` per **(user, participation)**, allocated by `AdVpnKeys.AssignNextIp` skipping the network and server addresses and stopping one short of broadcast.

IP assignment is **global across all games** — every peer renders onto one shared `wg0` interface, so two games can never collide on the same client IP.

The generated client config's `AllowedIPs` is resolved in this order of precedence:

1. An explicit `Ad__Vpn__AllowedIps` env override (operator knows best).
2. The VPN subnet **plus** the live-discovered challenge subnets from `AdVpnTopology` — the path that "just works".
3. Fallback to just the VPN subnet (degraded mode: clients can only reach each other, not challenges).

The server side NATs VPN traffic (`iptables ... POSTROUTING -s 10.13.37.0/24 -j MASQUERADE`) so return packets from challenge containers find their way back regardless of which sidecar interface they egress through.

### Fetching your config

```text
GET /api/Game/{id}/Ad/Vpn/Config
```

- Requires a logged-in user (`[RequireUser]`) who is an **Accepted** member of a participation in game `{id}`; otherwise `403`.
- The game must have at least one Attack & Defense or King of the Hill challenge, else `404`.
- On the **first** call it generates a fresh X25519 keypair, allocates the next free `/32`, and persists an `AdVpnPeer` row. Subsequent calls return the **same** peer's `.conf`, so it keeps matching the server-side entry rendered into `wg0.conf`.
- The private key is stored at-rest XOR-wrapped (`XorKey`); the server config only ever holds your **public** key.

The response is a downloadable `.conf` file named `ad-game-{id}-{yourname}.conf`, shaped like this:

```ini
# WireGuard config for alice — A&D game 1
# Generated 2026-05-29 12:00:00Z
# Pubkey: <your base64 pubkey>
# Assigned IP: 10.13.37.2

[Interface]
PrivateKey = <your base64 private key>
Address = 10.13.37.2/32
DNS = 1.1.1.1

[Peer]
PublicKey = <server pubkey from /wg-config/server.pub>
Endpoint = 1pc.tf:51820
AllowedIPs = 10.13.37.0/24, 172.x.y.0/24
PersistentKeepalive = 25
```

Bring it up like any WireGuard config:

```bash
# Download via the Toolkit, or with your session cookie:
curl -b cookies.txt -OJ https://gzctf.gzti.me/api/Game/1/Ad/Vpn/Config

sudo wg-quick up ./ad-game-1-alice.conf
# now you can reach your box and opponents directly:
curl http://10.x.x.x:PORT/
```

:::info
The `Endpoint` (`1pc.tf:51820` here) comes from `Ad__Vpn__ServerEndpoint`. The `wireguard` sidecar publishes UDP **51820** to the host. The in-code default (`127.0.0.1:51820`) is host-only — operators **must** override `Ad__Vpn__ServerEndpoint` so external clients can dial in.
:::

:::warning
If you fetch the config and get a `503` ("WireGuard server keypair not yet provisioned"), the `wireguard` sidecar hasn't generated `server.pub`/`server.key` into the shared volume yet, or the volume isn't mounted at `Ad__Vpn__ConfigDir` (`/wg-config`). This is an operator-side condition; wait a few seconds or check the sidecar.
:::

### Revoke-on-kick

There is no explicit "revoke VPN" button — revocation is **automatic and authoritative**. Every 15 s the reconciler only renders a peer whose row satisfies:

```csharp
p.RevokedAt == null
&& db.Participations.Any(part =>
    part.Id == p.ParticipationId
    && part.Status == ParticipationStatus.Accepted
    && part.Members.Any(m => m.UserId == p.UserId))
```

So the moment a member is **kicked**, **leaves**, or the participation stops being **Accepted** (suspended/declined), their `[Peer]` block stops being written, the sidecar runs `wg syncconf`, and the tunnel dies within one tick. This is the same "still a member" guarantee enforced by the team API token and the SSH path — a stale config can't keep reaching opponents' boxes.

---

## SSH access via the jump host

### What the `ssh-jump` host is

`ssh-jump` is a hardened OpenSSH bastion (a compose service) that publishes host port **22022** (`AD_SSH_PUBLIC_PORT`) onto its internal port 22. It has **no real shell accounts**:

- The **username** you SSH as is the **challenge id** you want to land in (e.g. `ssh 76@host`).
- Your **SSH public key** identifies **who you are** (which team's container you get).
- After auth, a `ForceCommand` opens a WebSocket back to gzctf and proxies your stdio straight into a `docker exec` session in the resolved container. There is no shell, no scp, no port-forwarding, and no agent forwarding on the box itself (`AllowTcpForwarding no`, `PubkeyAuthentication yes`, `PasswordAuthentication no`).

```text
player ──ssh <challengeId>@host:22022──▶ ssh-jump
   │  (AuthorizedKeysCommand: lookup-key.sh)
   │      └─ GET /api/Internal/Ad/Ssh/Lookup  (X-Gzctf-Internal-Auth)
   │
   └─ on success → ForceCommand exec.sh → WS /api/Internal/Ad/Ssh/Exec
                                              └─ docker exec sh  ◀── your container
```

`ssh-jump` learns which challenge ids are valid usernames from gzctf: every 60 s `refresh-users.sh` calls `GET /api/Internal/Ad/Ssh/Challenges` and writes one `/etc/passwd` alias per active A&D challenge id (all UID 1001, shell `/bin/sh`, marked `# gzctf-ad-managed`). Host-level isolation is none; the real isolation is downstream, in the container the `ForceCommand` attaches you to.

### The internal auth boundary

`InternalAdSshController` (`/api/Internal/Ad/Ssh/*`) is **east-west only** — never public. Both `ssh-jump` and gzctf share a secret (`Ad__Ssh__InternalSecret`), sent as the `X-Gzctf-Internal-Auth` header (or `auth` query param on the WebSocket `/Exec`). The compose docker network is the only routable path, and the controller **refuses to operate at all** if the secret is unset. Operators should generate it with `openssl rand -hex 32`.

### Registering your SSH public key

Two ways, both scoped to one slot per **(user, game)** (unique `(UserId, ParticipationId)` index). Re-registering rotates the key.

**1. Upload your own public key (recommended — private half never leaves your machine):**

```text
POST /api/Game/{id}/Ad/Ssh/Key
Content-Type: application/json

{ "publicKey": "ssh-ed25519 AAAAC3Nza... alice@laptop" }
```

The key is parsed and validated; only `ssh-ed25519`, `ssh-rsa`, and the ECDSA algorithms `ecdsa-sha2-nistp256` / `ecdsa-sha2-nistp384` / `ecdsa-sha2-nistp521` are accepted, and the SHA256 fingerprint is computed and stored. Returns an `AdSshKeyInfoModel`.

**2. Have the platform generate one (ed25519):**

```text
POST /api/Game/{id}/Ad/Ssh/Key/Generate
```

Returns the private key **once** in the response body (it is also stored XOR-wrapped at-rest so the platform can re-emit it if you lose the file before the game ends):

```json
{
  "algorithm": "ssh-ed25519",
  "publicKey": "ssh-ed25519 AAAAC3Nza... gzctf-ad-alice-game1",
  "privateKey": "-----BEGIN OPENSSH PRIVATE KEY-----\n...\n",
  "fingerprint": "SHA256:...",
  "createdAt": "2026-05-29T12:00:00Z"
}
```

**Inspect / revoke:**

```text
GET    /api/Game/{id}/Ad/Ssh/Key   → metadata only (Exists, Algorithm, Fingerprint,
                                      PlatformGenerated, CreatedAt, LastUsedAt, JumpHost)
DELETE /api/Game/{id}/Ad/Ssh/Key   → 204; next connection from that key is refused
```

`GetSshKey` returns `Exists: false` (plus the `JumpHost` string) when you have no key or it's revoked, so the UI can render the upload form. `JumpHost` is resolved from `Ad__Ssh__PublicHost` + `Ad__Ssh__PublicPort` (e.g. `1pc.tf:22022`).

All of these require an **Accepted** member of the game (`403` otherwise).

### Connecting

```bash
# Land in your team's container for challenge id 76:
ssh -i ~/.ssh/id_ed25519 -p 22022 76@1pc.tf
```

What happens on the bastion: sshd calls `lookup-key.sh` with your offered key. It rejects non-numeric usernames locally, then asks gzctf's `/Lookup` to resolve `(fingerprint, challengeId)` to a live container. On `200` it emits a single `authorized_keys` line whose `ForceCommand` pins the resolved `(containerGuid, userId, challengeId)`; on anything else it emits nothing and the connection is refused.

`/Lookup` returns `404` (= reject the SSH connection) whenever:

- the fingerprint matches no **registered, non-revoked** key for an **Accepted** participation in that challenge's game,
- the key's owner is **no longer on the team roster** (member-kick = instant revocation, same as the VPN and API-token paths), or
- no container is running for that team's service yet.

:::warning Ambiguous-key rejection
`/Lookup` does a `Take(2)` on matching keys and **fails closed** if more than one participation in the same game registered the **same** key fingerprint. If two teams uploaded the same public key, neither can SSH in for that challenge — the platform refuses rather than guess which team's box to open. Use a unique key per player/team.
:::

You can only SSH into **your own** container. The SSH path is for defending and inspecting your service; reaching opponents is the VPN's job.

---

## The in-game Toolkit (sidebar)

Inside a game, the **Toolkit** sidebar collects everything a team needs to play A&D / KotH. It is backed entirely by the endpoints above.

| Toolkit item | Endpoint(s) | What it's for |
| --- | --- | --- |
| **Team API token** | `POST /api/Game/{id}/Ad/Token` (rotate, returns plaintext once), `GET /api/Game/{id}/Ad/Token` (hint only) | The `Bearer ad_...` token used by exploit scripts to `Submit` flags and fetch the KotH control token without a browser. |
| **VPN config** | `GET /api/Game/{id}/Ad/Vpn/Config` | Download your `.conf` (see above). |
| **SSH key** | `GET` / `POST` / `POST .../Generate` / `DELETE` on `/api/Game/{id}/Ad/Ssh/Key` | Upload, generate, inspect, or revoke your jump-host key; shows the `JumpHost` to connect to. |
| **Targets** | `GET /api/Game/{id}/Ad/Targets` | The `ip:port` list of opponents' boxes (and the KotH hill). |

### The team API Bearer token

The token is per-user, prefixed **`ad_`**, and any team member can rotate their own (no captain check). `RotateToken` returns the plaintext exactly once and stores only a hash (XOR-keyed) plus a stable `Hint` like `ad_a1b2…f9e8`. Use it as a normal bearer credential:

```bash
# Submit captured flags (scripted, no cookie):
curl -X POST https://gzctf.gzti.me/api/Game/1/Ad/Submit \
  -H "Authorization: Bearer ad_xxxxxxxx" \
  -H "Content-Type: application/json" \
  -d '{"flags": ["flag{...}", "flag{...}"]}'
```

The same token authenticates the **KotH control token** endpoint:

```text
GET /api/Game/{id}/Ad/Koth/Token
```

It returns your team's game-wide control token; write that exact value into a hill's `/koth/king` marker to claim it. The token is the **same for every hill** and rotates only when the hills reset (every `KothRefreshTicks` rounds, default 5), so fetch it once per refresh window and plant it on whichever hills you take. A per-challenge form `GET /api/Game/{id}/Ad/Koth/{challengeId}/Token` still works and returns the same token. See [/guide/features/king-of-the-hill](/guide/features/king-of-the-hill).

:::tip
Anywhere the token is accepted, a logged-in session cookie works too. The bearer token exists so exploit and KotH-holder scripts can run headless. Endpoints accepting both call `ResolveTeamApiTokenAsync` (the `Bearer ad_...` path) first, then fall back to the cookie session.
:::

### The Targets list

```text
GET /api/Game/{id}/Ad/Targets
```

Dual auth (cookie **or** `Bearer ad_...`), so it's pollable from a script. It lists, per enabled A&D / KotH challenge, **every other** team's container `ip:port` (your own team rows are excluded — the response is directly usable as an aim list), along with each box's last health-check verdict. During the warm-up (round 0) it returns an empty `challenges[]`.

```json
{
  "currentRound": 3,
  "challenges": [
    {
      "challengeId": 76,
      "title": "notes-php",
      "tickSeconds": 60,
      "teams": [
        { "participationId": 12, "teamName": "Blue", "division": "Open",
          "ip": "172.x.y.5", "port": 8080, "lastCheckStatus": "Up" }
      ],
      "hill": null
    },
    {
      "challengeId": 91,
      "title": "the-hill",
      "tickSeconds": 60,
      "teams": [],
      "hill": { "ip": "172.x.y.20", "port": 9000,
                "lastCheckStatus": "Up", "lastRefreshRound": 0 }
    }
  ]
}
```

For King of the Hill challenges, `teams[]` is empty (the hill is a single shared container) and the shared target is reported under `hill` instead — including `lastRefreshRound`, so you can tell when the hill's IP last rotated and re-aim accordingly.

:::tip
These IPs are only reachable once your **WireGuard VPN** is up (or from a host already on the challenge subnets). Combine the Toolkit pieces: bring up the VPN, poll `Targets` for opponents' `ip:port`, run your exploit, then `Submit` the captured flags with your `ad_` token.
:::

### The KotH hills list

```text
GET /api/Game/{id}/Ad/Koth/Hills
```

The KotH-focused counterpart to `Targets`: one call returns **every** enabled hill's name, target `ip:port`, current holder, and functional status (no challenge id needed). It's the recommended way to confirm a plant took and to feed a bot all hills at once. Returns an array of `KothHillStateModel` — each element carries `challengeId`, `title`, `holderTeamName`, `isYou`, `status`, `ip`, `port`, and `lastRefreshRound`; see [King of the Hill](/guide/features/king-of-the-hill) for the full shape. (Where `Targets` mixes A&D boxes and the hill together, `Hills` returns only KotH hills with dedicated holder/status fields.) For a single hill, `GET /api/Game/{id}/Ad/Koth/{challengeId}/State` returns the same fields.

---

## Configuration reference

These env vars (compose / `appsettings` — see [/config/appsettings](/config/appsettings)) drive the features on this page:

| Env var | Default | Meaning |
| --- | --- | --- |
| `Ad__Vpn__ConfigDir` | `/wg-config` | Shared volume where the sidecar writes `server.pub`/`server.key` and gzctf writes `wg0.conf`. Must match the sidecar's `/config`. |
| `Ad__Vpn__ClientCidr` | `10.13.37.0/24` | VPN peer subnet (must be at least `/30`). |
| `Ad__Vpn__ListenPort` | `51820` | Server-side WireGuard UDP port. |
| `Ad__Vpn__ServerEndpoint` | `127.0.0.1:51820` | Host-reachable UDP endpoint clients dial. **Override for external access** (compose sets `1pc.tf:51820`). |
| `Ad__Vpn__Dns` | `1.1.1.1` | `DNS` line in the client config. |
| `Ad__Vpn__AllowedIps` | (unset) | Optional `AllowedIPs` override; otherwise auto-discovered. |
| `Ad__Ssh__InternalSecret` | (none — required) | Shared secret between gzctf and `ssh-jump`. Generate with `openssl rand -hex 32`. |
| `Ad__Ssh__PublicHost` | `1pc.tf` (compose) | Host players SSH to (falls back to `PublicEntry`, else `localhost`). |
| `Ad__Ssh__PublicPort` | `22022` (compose) | Host port for the jump host (falls back to `2222` in the displayed `JumpHost`). |

:::danger
Leaving `Ad__Ssh__InternalSecret` at the compose dev default (`dev-only-rotate-me-before-prod`) means anything able to reach the gzctf container on the docker network could drive the SSH lookup/exec endpoints. Always set a strong secret before a real event, and use the same value on both the `gzctf` and `ssh-jump` services.
:::

Related reading: [/guide/features/attack-defense](/guide/features/attack-defense) · [/guide/features/king-of-the-hill](/guide/features/king-of-the-hill) · [/guide/features/scoring](/guide/features/scoring) · [/guide/authoring/challenge-yaml](/guide/authoring/challenge-yaml)
