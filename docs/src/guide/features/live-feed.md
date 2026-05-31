# Live event feed (WebSocket)

Every game has a **live feed** of attack and King-of-the-Hill events — the same stream that drives the animation on `/games/{id}/attack`. Participants can tap it from a script, bot, or custom overlay.

There are two ways to consume it:

| | Plain WebSocket | SignalR hub |
| --- | --- | --- |
| Endpoint | `GET /hub/attack/ws?game={id}` | `/hub/attack?game={id}` |
| Protocol | one JSON object per WS frame | SignalR (negotiate + framing) |
| Client | any WebSocket library, or `websocat` | `@microsoft/signalr` / `signalrcore` |
| Best for | **bots, scripts, overlays** | the built-in web UI |

The **plain WebSocket** is the easy one — no negotiate handshake, no SignalR protocol, no auth token. This page documents it. (The SignalR hub is unchanged and still powers the in-app animation; the two carry identical events.)

## Connecting

```text
GET wss://<host>/hub/attack/ws?game={gameId}
```

- Use `wss://` over HTTPS, `ws://` over plain HTTP (e.g. `ws://localhost:8080/hub/attack/ws?game=19`).
- **No authentication.** The feed is public — anyone can watch any *visible* game. (Draft/`Hidden` games are monitor-only, same as the rest of the public surface.)
- The server **pushes** frames; the client sends nothing. Anything you send is ignored.
- Bad/missing `game`, or a game you can't see, closes the connection (HTTP `400`/`404` on the upgrade).

The connection stays open for the whole game; reconnect if it drops (a slow client may be disconnected — see *Backpressure* below).

## Frames

Each frame is **one JSON object** with a `kind` field telling you what it is:

### `hello` — sent once, on connect

```json
{ "kind": "hello", "game": 19, "events": ["attack", "koth"] }
```

### `ping` — keepalive, ~every 25s when idle

```json
{ "kind": "ping" }
```

Ignore it (or use it as a liveness check). It exists so reverse proxies don't idle-drop the socket.

### `attack` — a flag submission (A&D capture or jeopardy solve)

```json
{
  "kind": "attack",
  "teamName": "Team 08",
  "teamAvatar": "/assets/<hash>/avatar",
  "teamScore": null,
  "challengeTitle": "owasp-portal",
  "category": "Web",
  "type": "FirstBlood",
  "time": "2026-05-31T12:00:00Z",
  "victimTeamName": "Team 03"
}
```

| Field | Meaning |
| --- | --- |
| `teamName` | Submitting team. |
| `teamAvatar` | Relative avatar URL, or `null`. |
| `teamScore` | Submitter's current score if known, else `null`. |
| `challengeTitle` | Target challenge / service. |
| `category` | `Web`, `Pwn`, `Crypto`, … |
| `type` | `FirstBlood`, `Normal`, … (the submission type — drives particle colour in the UI). |
| `time` | Submission time (UTC, ISO-8601). |
| `victimTeamName` | **A&D only**: the team whose flag was stolen. `null` for jeopardy solves (which target the central HQ). |

### `koth` — a King-of-the-Hill control change

Fired when a hill's holder changes (a takeover, or a hill going uncontrolled).

```json
{
  "kind": "koth",
  "challengeId": 263,
  "challengeTitle": "koth-pwn",
  "round": 615,
  "holderTeamName": "Team 10",
  "holderTeamAvatar": "/assets/<hash>/avatar",
  "previousTeamName": "Team 08",
  "status": "Ok"
}
```

| Field | Meaning |
| --- | --- |
| `challengeId` | The hill's challenge id (stable key). |
| `challengeTitle` | Hill name. |
| `round` | Round this verdict was scored for. |
| `holderTeamName` | New holder, or `null` if the hill went uncontrolled. |
| `holderTeamAvatar` | New holder's avatar URL, or `null`. |
| `previousTeamName` | Who held it the previous tick, or `null`. |
| `status` | Functional verdict on the hill — `Ok` / `Mumble` / `Offline` / `InternalError`. |

:::info
**Freeze & visibility.** Events are subject to the same gate as the scoreboard: nothing is emitted for `Hidden` games, and during an ICPC-style freeze window the feed goes quiet for non-monitors — the same rule that hides late-game state on the frozen board.
:::

## Examples

### `websocat` (shell)

```bash
websocat "wss://<host>/hub/attack/ws?game=19"
# every line is one JSON event — pipe into jq:
websocat "wss://<host>/hub/attack/ws?game=19" | jq -c 'select(.kind=="attack")'
```

### Browser / Node

```js
const ws = new WebSocket('wss://<host>/hub/attack/ws?game=19')
ws.onmessage = (e) => {
  const evt = JSON.parse(e.data)
  if (evt.kind === 'attack') {
    console.log(`${evt.teamName} → ${evt.victimTeamName ?? 'HQ'} : ${evt.challengeTitle} (${evt.type})`)
  } else if (evt.kind === 'koth') {
    console.log(`KotH ${evt.challengeTitle}: ${evt.previousTeamName ?? '—'} → ${evt.holderTeamName ?? 'uncontrolled'}`)
  }
}
```

### Python (`pip install websockets`)

```python
import asyncio, json, websockets

async def main():
    url = "wss://<host>/hub/attack/ws?game=19"
    async with websockets.connect(url) as ws:
        async for raw in ws:
            evt = json.loads(raw)
            if evt["kind"] == "attack":
                tgt = evt.get("victimTeamName") or "HQ"
                print(f'{evt["teamName"]} -> {tgt}: {evt["challengeTitle"]} ({evt["type"]})')
            elif evt["kind"] == "koth":
                print(f'KotH {evt["challengeTitle"]}: -> {evt.get("holderTeamName") or "uncontrolled"}')

asyncio.run(main())
```

## Notes

- **Backpressure.** Each connection has a bounded outbound queue; if your client can't keep up, the *oldest* events are dropped (never the broadcaster — one slow client can't stall the feed for others). Keep your handler fast, or buffer on your side.
- **Ordering.** Events are delivered in broadcast order per connection. There is no replay/backfill — you only get events that occur while you're connected. For historical data use the REST scoreboard / timeline (`/api/Game/{id}/Ad/Scoreboard`, `/api/Game/{id}/Ad/Timeline`).
- **It's a mirror.** The plain-WS feed and the SignalR hub publish the same events at the same time; pick whichever client is easier for your tooling.

See also: [Attack & Defense](/guide/features/attack-defense) for the attack/submit/targets REST API, and [Scoring](/guide/features/scoring) for how these events turn into points.
