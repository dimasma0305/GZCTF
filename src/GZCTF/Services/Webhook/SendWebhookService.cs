using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GZCTF.Models.Data;
using Microsoft.Extensions.Logging;

namespace GZCTF.Services.Webhook;

public class Models
{
    public class DiscordWebhookMessage
    {
        public string? Content { get; set; }
        public string? Username { get; set; }
        public string? AvatarUrl { get; set; }
        public List<DiscordEmbed>? Embeds { get; set; }
    }

    public class DiscordEmbed
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public int? Color { get; set; }
        public List<DiscordEmbedField>? Fields { get; set; }
        public DiscordEmbedFooter? Footer { get; set; }
        public string? Timestamp { get; set; }
    }

    public class DiscordEmbedField
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public bool? Inline { get; set; }
    }

    public class DiscordEmbedFooter
    {
        public string Text { get; set; } = string.Empty;
    }
}

public class SendWebhookService(ILogger<SendWebhookService> logger) : ISendWebhookService
{
    private const int ContentLimit = 2000;
    private const int EmbedTitleLimit = 256;
    private const int EmbedDescriptionLimit = 4096;
    private const int EmbedFooterLimit = 2048;
    private const int EmbedFieldNameLimit = 256;
    private const int EmbedFieldValueLimit = 1024;
    private const int EmbedFieldCountLimit = 25;
    private const int EmbedTotalCharLimit = 6000;

    // The webhook URL is operator-set, but a lower-trust per-game EventManager can set
    // it too — so the outbound POST must not become an SSRF into internal services or
    // cloud metadata. We resolve the target ourselves and refuse to connect to any
    // non-public-unicast address; validating at CONNECT time (not just up front) also
    // defeats DNS rebinding, since we connect to the exact address we vetted. Redirects
    // are disabled so a benign public host can't 30x us into internal space.
    private static readonly HttpClient WebhookClient = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (ctx, ct) =>
            {
                var host = ctx.DnsEndPoint.Host;
                var addrs = IPAddress.TryParse(host, out var literal)
                    ? [literal]
                    : await Dns.GetHostAddressesAsync(host, ct);
                var target = Array.Find(addrs, a => !IsBlockedAddress(a))
                    ?? throw new IOException($"Webhook host '{host}' does not resolve to a public address");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(target, ctx.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// True for any address a webhook must NOT reach: loopback, link-local (incl. the
    /// 169.254.169.254 cloud-metadata endpoint), RFC1918 / CGNAT / ULA private ranges,
    /// multicast/reserved, and the unspecified address. Only public unicast passes.
    /// </summary>
    internal static bool IsBlockedAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] is 0 or 10 or 127) return true;                 // this-network, RFC1918 10/8, loopback
            if (b[0] == 169 && b[1] == 254) return true;             // link-local incl. cloud metadata
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return true; // 172.16/12
            if (b[0] == 192 && b[1] == 168) return true;             // 192.168/16
            if (b[0] == 100 && b[1] is >= 64 and <= 127) return true;// 100.64/10 CGNAT
            return b[0] >= 224;                                       // 224/4 multicast + 240/4 reserved
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Normalize embedded-IPv4 IPv6 forms (6to4 / NAT64 / IPv4-compatible) to their
            // IPv4 and re-classify, so an internal target can't be reached via those wrappers.
            var embedded = ExtractEmbeddedIPv4(ip);
            if (embedded is not null) return IsBlockedAddress(embedded);

            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
            return (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;         // fc00::/7 unique-local
        }

        return true; // unknown address family — refuse
    }

    /// <summary>Extract the embedded IPv4 from 6to4 (2002::/16), NAT64 (64:ff9b::/96), or
    /// IPv4-compatible (::a.b.c.d) IPv6 addresses; null if not one of those forms.</summary>
    private static IPAddress? ExtractEmbeddedIPv4(IPAddress ip)
    {
        var b = ip.GetAddressBytes(); // 16 bytes
        if (b[0] == 0x20 && b[1] == 0x02)                              // 6to4 2002:AABB:CCDD::
            return new IPAddress(new[] { b[2], b[3], b[4], b[5] });
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b // NAT64 64:ff9b::/96
            && b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0
            && b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0)
            return new IPAddress(new[] { b[12], b[13], b[14], b[15] });
        var hiZero = true;
        for (var i = 0; i < 12 && hiZero; i++) hiZero = b[i] == 0;
        if (hiZero && b[12] != 0)                                       // IPv4-compatible ::a.b.c.d
            return new IPAddress(new[] { b[12], b[13], b[14], b[15] });
        return null;
    }

    public async Task SendGameEventAsync(GameEvent gameEvent, string webhookUrl)
    {
        try
        {
            var message = CreateMessage(gameEvent);
            if (message == null) return;

            await SendAsync(webhookUrl, message, "event");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending webhook");
        }
    }

    public async Task SendNoticeAsync(GameNotice notice, string webhookUrl)
    {
        try
        {
            var message = CreateNoticeMessage(notice);
            if (message == null) return;

            await SendAsync(webhookUrl, message, "notice");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending webhook notice");
        }
    }

    private async Task SendAsync(string webhookUrl, Models.DiscordWebhookMessage message, string kind)
    {
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            logger.LogWarning("Skip invalid Discord webhook URL for {Kind}", kind);
            return;
        }

        SanitizeMessage(message);

        using var content = new StringContent(
            JsonSerializer.Serialize(message, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            }),
            System.Text.Encoding.UTF8,
            "application/json");

        using var response = await WebhookClient.PostAsync(uri, content);

        if (response.IsSuccessStatusCode)
            return;

        // Deliberately NOT logging the response body: with the SSRF guard the target is
        // a public host, but echoing an arbitrary remote response into operator logs is
        // an unnecessary read primitive. Status + reason are enough to diagnose.
        logger.LogError(
            "Failed to send webhook {Kind}: {StatusCode} {Reason}",
            kind,
            (int)response.StatusCode,
            response.ReasonPhrase ?? "Unknown");
    }

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text ?? string.Empty;

        var cut = text[..maxLength];
        // Don't slice in the middle of a backslash-escape pair (from EscapeMd) — a dangling
        // trailing backslash would render literally. Drop an orphaned (odd) trailing run.
        var bs = 0;
        for (var i = cut.Length - 1; i >= 0 && cut[i] == '\\'; i--) bs++;
        if ((bs & 1) == 1) cut = cut[..^1];
        return cut;
    }

    /// <summary>
    /// Backslash-escape Discord markdown control chars so a player-chosen team name (or a
    /// challenge title set by a lower-trust per-game EventManager) can't inject bold/links/
    /// formatting into the staff channel when interpolated into an embed.
    /// </summary>
    private static string EscapeMd(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new System.Text.StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '[' or ']' or '(' or ')' or '>' or '#')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static void SanitizeMessage(Models.DiscordWebhookMessage message)
    {
        message.Content = Truncate(message.Content, ContentLimit);

        if (message.Embeds is not { Count: > 0 })
            return;

        foreach (var embed in message.Embeds)
        {
            embed.Title = Truncate(embed.Title, EmbedTitleLimit);
            embed.Description = Truncate(embed.Description, EmbedDescriptionLimit);

            if (embed.Footer is not null)
                embed.Footer.Text = Truncate(embed.Footer.Text, EmbedFooterLimit);

            if (embed.Fields is { Count: > 0 })
            {
                if (embed.Fields.Count > EmbedFieldCountLimit)
                    embed.Fields = embed.Fields.Take(EmbedFieldCountLimit).ToList();

                foreach (var field in embed.Fields)
                {
                    field.Name = Truncate(field.Name, EmbedFieldNameLimit);
                    field.Value = Truncate(field.Value, EmbedFieldValueLimit);
                }
            }

            var totalChars = (embed.Title?.Length ?? 0) +
                             (embed.Description?.Length ?? 0) +
                             (embed.Footer?.Text.Length ?? 0) +
                             (embed.Fields?.Sum(f => f.Name.Length + f.Value.Length) ?? 0);

            if (totalChars > EmbedTotalCharLimit && !string.IsNullOrEmpty(embed.Description))
            {
                var overflow = totalChars - EmbedTotalCharLimit;
                embed.Description = Truncate(embed.Description, Math.Max(0, embed.Description.Length - overflow));
            }
        }
    }

    private Models.DiscordWebhookMessage? CreateMessage(GameEvent gameEvent)
    {
        // Only handle specific events to avoid noise
        if (gameEvent.Type != EventType.FlagSubmit && 
            gameEvent.Type != EventType.CheatDetected)
        {
            return null; 
        }

        var embed = new Models.DiscordEmbed
        {
            Timestamp = gameEvent.PublishTimeUtc.ToString("o"),
        };

        switch (gameEvent.Type)
        {
            case EventType.FlagSubmit:
                // User requested to ONLY notify for First/Second/Third Blood.
                // Bloods are handled via GameNotice (CreateNoticeMessage), so we disable
                // the generic "Challenge Solved!" notification here entirely.
                return null;
            case EventType.CheatDetected:
                embed.Title = "Cheat Detected! 🚨";
                embed.Color = 0xFF0000; // Red
                embed.Description = $"Cheat detected for team **{EscapeMd(gameEvent.Team?.Name)}**.\nDetails: {EscapeMd(string.Join(", ", gameEvent.Values ?? []))}";
                embed.Footer = new Models.DiscordEmbedFooter { Text = gameEvent.Game?.Title ?? "Unknown Game" };
                break;
            default:
                 return null;
        }

        return new Models.DiscordWebhookMessage
        {
            Embeds = new List<Models.DiscordEmbed> { embed }
        };
    }

    private Models.DiscordWebhookMessage? CreateNoticeMessage(GameNotice notice)
    {
        var embed = new Models.DiscordEmbed
        {
            Timestamp = notice.PublishTimeUtc.ToString("o")
        };

        // Custom formatting for Blood notices
        if (notice.Type is NoticeType.FirstBlood or NoticeType.SecondBlood or NoticeType.ThirdBlood)
        {
            switch (notice.Type)
            {
                case NoticeType.FirstBlood:
                    embed.Title = "First Blood! 🥇";
                    embed.Color = 0xFFD700; // Gold
                    break;
                case NoticeType.SecondBlood:
                    embed.Title = "Second Blood! 🥈";
                    embed.Color = 0xC0C0C0; // Silver
                    break;
                case NoticeType.ThirdBlood:
                    embed.Title = "Third Blood! 🥉";
                    embed.Color = 0xCD7F32; // Bronze
                    break;
            }
            
            // Values: [TeamName, ChallengeName] — both user/author-controlled, so escape
            // Discord markdown before interpolating into the embed.
            var teamName = EscapeMd(notice.Values?.ElementAtOrDefault(0) ?? "Unknown Team");
            var challengeName = EscapeMd(notice.Values?.ElementAtOrDefault(1) ?? "Unknown Challenge");
            
            string prefix = notice.Type switch
            {
                NoticeType.FirstBlood => "First Blood! ",
                NoticeType.SecondBlood => "Second Blood! ",
                NoticeType.ThirdBlood => "Third Blood! ",
                _ => ""
            };

            embed.Description = $"{prefix}**{teamName}** solved **{challengeName}**";
            embed.Footer = new Models.DiscordEmbedFooter { Text = notice.Game?.Title ?? "Unknown Game" };
        }
        else
        {
             // Standard notices
             embed.Title = $"{notice.Type} 🎯";
             embed.Color = notice.Type switch
             {
                 NoticeType.NewHint => 0x3498DB,      // Blue
                 NoticeType.NewChallenge => 0x2ECC71, // Green
                 _ => 0x95A5A6                        // Gray for others
             };
             
             embed.Description = notice.Type switch
             {
                 NoticeType.NewChallenge => $"New challenge released: **{EscapeMd(notice.Values?.FirstOrDefault())}**",
                 NoticeType.NewHint => $"New hint released for challenge **{EscapeMd(notice.Values?.FirstOrDefault())}**",
                 NoticeType.Normal => notice.Values?.FirstOrDefault() ?? "New announcement",
                 _ => notice.Values?.Count > 0 ? string.Join(", ", notice.Values) : notice.Type.ToString()
             };
             embed.Footer = new Models.DiscordEmbedFooter { Text = notice.Game?.Title ?? "Unknown Game" };
        }

        return new Models.DiscordWebhookMessage
        {
            Embeds = new List<Models.DiscordEmbed> { embed }
        };
    }
}
