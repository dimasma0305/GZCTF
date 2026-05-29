# Webhooks

GZ::CTF (this fork) can push live game events to an external HTTP endpoint as they happen. Each game carries its own webhook URL, so the platform fires an HTTP `POST` with a Discord-compatible JSON body whenever a noteworthy event occurs in that game — a blood is taken, a new challenge or hint drops, an admin posts an announcement, or the anti-cheat flags a team.

:::info
This webhook integration is **exclusive to this fork** — it does not exist in upstream GZ::CTF. The payload is shaped as a [Discord webhook message](https://discord.com/developers/docs/resources/webhook#execute-webhook), so it works out-of-the-box against a Discord channel webhook, but any endpoint that accepts a JSON `POST` (a SIEM ingest URL, a chat bridge, an automation runner) can consume it.
:::

## What webhooks are for

The feature exists to fan platform activity out to systems that live outside GZ::CTF:

- **Chat / ops visibility** — drop bloods, new releases, and announcements into a Discord channel (the native format) or anything that speaks the same shape.
- **SIEM / monitoring** — forward `Cheat Detected` events to a security pipeline.
- **Automation** — trigger downstream workflows when a challenge is released or a blood is scored.

The platform is the sender; your endpoint is the receiver. There is no inbound webhook, no callback, and no acknowledgement contract beyond the HTTP status code.

## How delivery works

When an event is recorded, the relevant repository looks up the game's configured webhook URL and, if one is set, fires the send **out-of-band** (`_ = webhookService.Send...`) so the game flow is never blocked or failed by a slow or broken endpoint.

- **Events** (`EventType`) are sent from `GameEventRepository.AddEvent` via `SendGameEventAsync`.
- **Notices** (`NoticeType`) are sent from `GameNoticeRepository.AddNotice` via `SendNoticeAsync` — but only when the notice is broadcast (`broadcast: true`).

The sender (`SendWebhookService`) has these characteristics:

| Property | Value | Source |
|---|---|---|
| HTTP method | `POST` | `WebhookClient.PostAsync` |
| Content type | `application/json; charset=utf-8` | `StringContent(..., "application/json")` |
| Allowed URL schemes | `http` and `https` only | `SendAsync` URL guard (`Uri.TryCreate` + `Scheme` check) |
| Request timeout | 10 seconds | `HttpClient.Timeout` |
| JSON casing | camelCase, null fields omitted | `JsonNamingPolicy.CamelCase`, `JsonIgnoreCondition.WhenWritingNull` |
| Retries | none | single `PostAsync` per event |

:::warning
There is **no signing, no HMAC, no secret header, and no custom header** of any kind. The only authentication is the secret embedded in the webhook URL itself (e.g. the token segment of a Discord webhook URL). Treat the URL as a credential, prefer `https`, and do not log or commit it. If your receiver needs to verify authenticity, put a secret token in the URL path/query and validate it server-side.
:::

If the endpoint returns a non-2xx status, the failure is logged server-side (status code, reason phrase, and the first 400 chars of the response body) and otherwise swallowed — the event is not retried or queued. Invalid (non-`http`/`https`) URLs are skipped with a warning.

## Which events fire a webhook

Although the platform records many internal event and notice types, the webhook sender deliberately filters down to a small, low-noise set. Anything not listed below produces **no** outbound webhook.

### Notices (`NoticeType`)

Every broadcast `GameNotice` is forwarded. The sender renders each type into a titled, colored embed:

| `NoticeType` | Fires webhook | Embed title | Color | Description content |
|---|---|---|---|---|
| `FirstBlood` (1) | Yes | `First Blood! 🥇` | Gold `0xFFD700` | `First Blood! **<team>** solved **<challenge>**` |
| `SecondBlood` (2) | Yes | `Second Blood! 🥈` | Silver `0xC0C0C0` | `Second Blood! **<team>** solved **<challenge>**` |
| `ThirdBlood` (3) | Yes | `Third Blood! 🥉` | Bronze `0xCD7F32` | `Third Blood! **<team>** solved **<challenge>**` |
| `NewHint` (4) | Yes | `NewHint 🎯` | Blue `0x3498DB` | `New hint released for challenge **<title>**` |
| `NewChallenge` (5) | Yes | `NewChallenge 🎯` | Green `0x2ECC71` | `New challenge released: **<title>**` |
| `Normal` (0) | Yes | `Normal 🎯` | Gray `0x95A5A6` | The announcement text |

Blood notices carry `Values = [teamName, challengeName]` (set in `GameNotice.FromSubmission`). `NewChallenge` / `NewHint` carry `Values = [title]`. `Normal` announcements carry the announcement text as the first value.

### Events (`EventType`)

Only one event type is forwarded:

| `EventType` | Fires webhook | Embed title | Color | Description content |
|---|---|---|---|---|
| `CheatDetected` (4) | Yes | `Cheat Detected! 🚨` | Red `0xFF0000` | `Cheat detected for team **<team>**.` + `Details: <comma-joined Values>` |
| `FlagSubmit` (3) | **No** | — | — | Explicitly suppressed in `CreateMessage` (returns `null`) — bloods are already covered by the notice path, so generic "challenge solved" events are not sent to avoid noise. |
| `Normal`, `ContainerStart`, `ContainerDestroy`, `Download`, `ChallengeOpened` | No | — | — | Not handled by the sender. |

For `CheatDetected`, the event's `Values` are `[challengeName, teamName, sourceTeamName]` (set in `FlagChecker`), and these are joined with `, ` into the `Details:` line. The embed footer is the game title.

## Payload shape

The body is a Discord webhook message object, serialized camelCase with nulls omitted. In practice every webhook this fork sends contains exactly **one embed** (no top-level `content`, `username`, or `avatarUrl` are set by the code).

```json
{
  "embeds": [
    {
      "title": "First Blood! 🥇",
      "description": "First Blood! **team-alpha** solved **Baby SQLi**",
      "color": 16766720,
      "footer": { "text": "My CTF 2026" },
      "timestamp": "2026-05-29T13:42:07.1234567+00:00"
    }
  ]
}
```

The full set of fields the sender can emit (from the `Models` classes in `SendWebhookService.cs`):

| Field | Type | Notes |
|---|---|---|
| `embeds[]` | array | Always present; always length 1 in current code |
| `embeds[].title` | string | Event/notice title (with emoji) |
| `embeds[].description` | string | Human-readable body, Markdown bold (`**...**`) |
| `embeds[].color` | int | Decimal RGB (the hex values in the tables above) |
| `embeds[].footer.text` | string | The game title |
| `embeds[].timestamp` | string | ISO-8601 round-trip (`"o"`) of the event/notice publish time (UTC) |
| `embeds[].fields[]` | array | Defined in the model but not populated by current event/notice rendering |
| `content`, `username`, `avatarUrl` | string | Defined in the model but not set; omitted from the JSON |

:::info
**Discord-compatible truncation.** Before sending, the message is clamped to Discord's documented limits: title ≤ 256, description ≤ 4096, footer ≤ 2048, field name ≤ 256, field value ≤ 1024, ≤ 25 fields, and a 6000-char total per embed (overflow is trimmed from the description). If you build a non-Discord consumer, expect these caps on long values.
:::

## How to configure it

The webhook URL is **per game**, not global. It is stored on the `Game` entity as `DiscordWebhook` (a nullable string, max length 255 — `Limits.UrlLength`) and edited through the same admin API/UI as the rest of a game's settings.

### Via the admin UI

In the admin panel, open **Games → (your game) → Info**. The **Discord Webhook** field (placeholder `https://discord.com/api/webhooks/...`, helper text "Send notifications to this webhook") sets the URL. Save the game to persist it. Leaving it blank disables webhooks for that game.

### Via the API

The field is exposed on the game-edit model as `discordWebhook` (`GameInfoModel.DiscordWebhook`) and is round-tripped on game create/update:

```json
{
  "title": "My CTF 2026",
  "discordWebhook": "https://discord.com/api/webhooks/123456789012345678/AbCdEf...",
  "start": "2026-06-01T00:00:00Z",
  "end": "2026-06-03T00:00:00Z"
}
```

```bash
# Update an existing game's webhook (admin session/cookie required)
curl -X PUT "https://gzctf.example.com/api/edit/games/1" \
  -H "Content-Type: application/json" \
  -b "GZCTF_Token=<admin-session-cookie>" \
  -d '{
        "title": "My CTF 2026",
        "discordWebhook": "https://discord.com/api/webhooks/123456789012345678/AbCdEf...",
        "start": "2026-06-01T00:00:00Z",
        "end": "2026-06-03T00:00:00Z"
      }'
```

Set `discordWebhook` to `null` or an empty string to turn webhooks off for that game (the sender only fires when the stored value has `Length > 0`).

:::tip
**There is no `appsettings.json` key for webhooks.** Webhooks are configured entirely as per-game data through the admin API/UI — they are *not* a static configuration block, environment variable, or section in [appsettings](/config/appsettings). Nothing about webhooks needs a service restart; changing the game's `discordWebhook` takes effect on the next event.
:::

## Testing your endpoint

Because the body is plain JSON over `POST`, you can validate any receiver with the exact shape the platform sends:

```bash
curl -X POST "https://your-endpoint.example.com/ingest" \
  -H "Content-Type: application/json" \
  -d '{
        "embeds": [
          {
            "title": "Cheat Detected! 🚨",
            "description": "Cheat detected for team **team-alpha**.\nDetails: Baby SQLi, team-alpha, team-bravo",
            "color": 16711680,
            "footer": { "text": "My CTF 2026" },
            "timestamp": "2026-05-29T13:42:07.0000000+00:00"
          }
        ]
      }'
```

To see real traffic end-to-end, point `discordWebhook` at a temporary request-capture URL (e.g. a request-bin service or a local listener), then trigger an event — score a first blood, release a challenge, or publish an announcement — and inspect the captured body.

:::danger
The webhook URL is a bearer credential. Anyone holding it can post to your channel/endpoint, and there is no additional signature to fall back on. Rotate it (re-issue the Discord webhook / your receiver token and update the game) if it is ever exposed in logs, exports, or screenshots.
:::

## Related

- [appsettings reference](/config/appsettings) — global platform configuration (note: webhooks are *not* configured here).
- [Scoring](/guide/features/scoring) — blood mechanics that drive `FirstBlood` / `SecondBlood` / `ThirdBlood` notices.
- [Attack & Defense](/guide/features/attack-defense) — runs in the same game model that carries the per-game `discordWebhook`.
- [Challenge YAML](/guide/authoring/challenge-yaml) — authoring challenges whose release triggers `NewChallenge` / `NewHint` notices.
