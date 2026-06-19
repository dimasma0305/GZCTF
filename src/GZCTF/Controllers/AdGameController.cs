using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using GZCTF.Extensions;
using Microsoft.AspNetCore.Authorization;
using GZCTF.Hubs;
using GZCTF.Hubs.Clients;
using GZCTF.Middlewares;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Models.Request.Game;
using GZCTF.Models.Response.Admin;
using GZCTF.Repositories.Interface;
using GZCTF.Services;
using GZCTF.Services.Cache;
using GZCTF.Services.Config;
using GZCTF.Storage.Interface;
using GZCTF.Utils;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace GZCTF.Controllers;

/// <summary>
/// Player-facing Attack &amp; Defense endpoints. Kept separate from
/// <see cref="GameController"/> to avoid bloating that file further.
/// All endpoints validate the caller is a member of an accepted
/// Participation in the target game.
/// </summary>
[ApiController]
[Route("api/Game/{id:int}/Ad")]
[Produces("application/json")]
[ProducesResponseType(typeof(RequestResponse), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(RequestResponse), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
public class AdGameController(
    AppDbContext db,
    UserManager<UserInfo> userManager,
    AdContainerManager adContainerManager,
    IBlobStorage blobStorage,
    IConfigService configService,
    IHubContext<AttackHub, IAttackClient> attackHub,
    Services.AttackStreamService attackStream,
    CacheHelper cacheHelper,
    IAdScoreboardRepository adScoreboard,
    IConfiguration configuration,
    IStringLocalizer<Program> localizer,
    ILogger<AdGameController> logger) : ControllerBase
{
    /// <summary>
    /// Submit one or more captured flags from other teams' services. Accepts
    /// session-cookie auth (for the web tooling) OR <c>Authorization: Bearer
    /// ad_...</c> for scripted exploits. Returns per-flag results in input
    /// order so callers can correlate.
    /// </summary>
    [HttpPost("Submit")]
    [EnableRateLimiting(nameof(RateLimiter.LimitPolicy.Submit))]
    [ProducesResponseType(typeof(AdBatchSubmitResultModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> SubmitBatch(int id, [FromBody] AdBatchSubmitModel model, CancellationToken token)
    {
        if (!ModelState.IsValid)
            return BadRequest(new RequestResponse(localizer[nameof(Resources.Program.Model_ValidationFailed)]));

        var attackerPart = await ResolveTeamApiTokenAsync(id, token)
                           ?? await ResolveUserParticipationAsync(id, token);

        if (attackerPart is null)
            return Unauthorized();

        // Submissions are only accepted inside the game window. Round-number
        // expiry alone doesn't close the door at game end (the scheduler
        // freezes the round number), so flags from the last lifetime window
        // would otherwise stay submittable forever and mutate final standings.
        var window = await db.Games
            .Where(g => g.Id == id)
            .Select(g => new { g.StartTimeUtc, g.EndTimeUtc, g.AdScoringPaused })
            .FirstOrDefaultAsync(token);
        if (window is null)
            return NotFound(new RequestResponse(localizer[nameof(Resources.Program.Game_NotFound)]));

        var now = DateTimeOffset.UtcNow;
        if (now < window.StartTimeUtc || now > window.EndTimeUtc)
        {
            var closed = model.Flags.Select(f => new AdSubmitResultModel
            {
                Flag = f,
                Status = now < window.StartTimeUtc ? "not_started" : "ended",
                Message = now < window.StartTimeUtc
                    ? "the game has not started yet"
                    : "the game has ended — submissions are closed"
            }).ToList();
            return Ok(new AdBatchSubmitResultModel { Results = closed });
        }

        // Scoring pause halts the round scheduler + checker (no new flags, no SLA
        // accrual); captures must freeze too, or attack points keep accruing while
        // the rest of the board is paused (e.g. during a contested ruling / outage).
        if (window.AdScoringPaused)
        {
            var paused = model.Flags.Select(f => new AdSubmitResultModel
            {
                Flag = f,
                Status = "paused",
                Message = "scoring is paused — submissions are not being recorded"
            }).ToList();
            return Ok(new AdBatchSubmitResultModel { Results = paused });
        }

        var currentRound = await db.AdRounds
            .Where(r => r.GameId == id)
            .OrderByDescending(r => r.Number)
            .FirstOrDefaultAsync(token);

        if (currentRound is null)
        {
            var notStarted = model.Flags.Select(f => new AdSubmitResultModel
            {
                Flag = f,
                Status = "not_started",
                Message = "No A&D round has started yet for this game"
            }).ToList();

            return Ok(new AdBatchSubmitResultModel { Results = notStarted });
        }

        var results = new List<AdSubmitResultModel>(model.Flags.Count);
        double totalPoints = 0;
        var acceptedCount = 0;

        foreach (var raw in model.Flags)
        {
            var result = await ProcessSingleFlagAsync(attackerPart, currentRound, raw, token);
            results.Add(result);
            if (result.Status == "accepted")
            {
                acceptedCount++;
                totalPoints += result.Points ?? 0;
            }
        }

        // A capture changes attack + defense totals — refresh the cached board.
        if (acceptedCount > 0)
            await cacheHelper.FlushAdScoreboardCache(id, token);

        return Ok(new AdBatchSubmitResultModel
        {
            AcceptedCount = acceptedCount,
            TotalPoints = totalPoints,
            Results = results
        });
    }

    /// <summary>
    /// Pull endpoint for the Kubernetes flag-writer sidecar. Virtual nodes can't
    /// <c>exec</c>, so the per-tick flag is <b>pulled</b>, not pushed: the sidecar
    /// polls this and writes the result to the read-only volume the challenge
    /// reads. Returns the current round's flag for one <c>(participation,
    /// challenge)</c> as <c>text/plain</c>.
    /// <para>Auth is the <paramref name="token"/> itself — an HMAC of the
    /// <c>(participation, challenge)</c> keyed by the platform XorKey, so it's
    /// unguessable and only ever yields the <em>owner's</em> own flag. Injected
    /// into the sidecar only (never the challenge container). No session needed.</para>
    /// </summary>
    [HttpGet("PodFlag/{participationId:int}/{challengeId:int}/{token}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> PodFlag(int id, int participationId, int challengeId, string token,
        CancellationToken cancelToken)
    {
        var expected = AdTokenUtils.Hash($"adpodflag:{participationId}:{challengeId}", configService.GetXorKey());
        byte[] presented;
        try { presented = Convert.FromHexString(token); }
        catch { return Unauthorized(); }
        if (!CryptographicOperations.FixedTimeEquals(expected, presented))
            return Unauthorized();

        var serviceId = await db.AdTeamServices
            .Where(s => s.ParticipationId == participationId && s.ChallengeId == challengeId
                && s.Participation.GameId == id)
            .Select(s => (int?)s.Id)
            .FirstOrDefaultAsync(cancelToken);
        if (serviceId is null)
            return NotFound();

        var flag = await db.AdFlags
            .Where(f => f.AdTeamServiceId == serviceId)
            .OrderByDescending(f => f.Id)
            .Select(f => f.Flag)
            .FirstOrDefaultAsync(cancelToken);

        // No round yet → the warmup literal, so the sidecar always writes something.
        return Content(flag ?? "flag{warmup-no-round-yet}", "text/plain");
    }

    /// <summary>
    /// BYOC (bring-your-own-container) agent tunnel. The team's agent opens an
    /// outbound WebSocket here; GZCTF bridges it byte-for-byte to the team's relay
    /// container's control port on the challenge bridge, where the relay runs the
    /// other end of a yamux session. The relay multiplexes inbound checker/attacker
    /// connections — and the rotating flag — back over this single tunnel, so the
    /// team needs only one outbound HTTPS connection (no public IP / inbound rule).
    /// <para>Auth is the <paramref name="token"/> — an HMAC of the
    /// <c>(participation, challenge)</c> keyed by the platform XorKey, scoped to
    /// that team's own relay. No session needed; the agent runs headless.</para>
    /// </summary>
    [HttpGet("Byoc/Agent/{participationId:int}/{challengeId:int}/{token}")]
    [AllowAnonymous]
    [EnableRateLimiting(nameof(RateLimiter.LimitPolicy.Concurrency))]
    [ProducesResponseType(StatusCodes.Status101SwitchingProtocols)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ByocAgent(int id, int participationId, int challengeId, string token,
        CancellationToken cancelToken)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
            return BadRequest(new RequestResponse(localizer[nameof(Resources.Program.Game_ChallengeNotFound)]));

        var expected = AdTokenUtils.Hash($"adbyocagent:{participationId}:{challengeId}", configService.GetXorKey());
        byte[] presented;
        try { presented = Convert.FromHexString(token); }
        catch { return Unauthorized(); }
        if (!CryptographicOperations.FixedTimeEquals(expected, presented))
            return Unauthorized();

        // Self-hosted challenge only — defense in depth (the reconciler only
        // launches a relay for AdSelfHosted, but never bridge to a normal box).
        var isByoc = await db.GameChallenges
            .AnyAsync(c => c.Id == challengeId && c.GameId == id && c.AdSelfHosted, cancelToken);
        if (!isByoc)
            return NotFound();

        var relayIpStr = await db.AdTeamServices
            .Where(s => s.ParticipationId == participationId && s.ChallengeId == challengeId
                && s.Participation.GameId == id)
            .Select(s => s.Container!.IP)
            .FirstOrDefaultAsync(cancelToken);
        if (string.IsNullOrEmpty(relayIpStr) || !IPAddress.TryParse(relayIpStr, out var relayIp))
            return NotFound();

        // yamux runs end-to-end between the agent and the relay; GZCTF is a
        // transparent pipe between the WebSocket and the relay's control TCP port.
        using var socket = new Socket(relayIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(relayIp, AdContainerManager.ByocCtlPort), cancelToken);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "BYOC agent: cannot reach relay {Ip}:{Port} for team={Tid} challenge={Cid}",
                relayIpStr, AdContainerManager.ByocCtlPort, participationId, challengeId);
            return NotFound();
        }

        // Authenticate to the relay's control port (it shares the challenge bridge
        // with jeopardy containers, so it trusts the secret, not the source IP).
        var relaySecret = AdTokenUtils.ByocRelaySecret(participationId, challengeId, configService.GetXorKey());
        try
        {
            await socket.SendAsync(
                System.Text.Encoding.ASCII.GetBytes(relaySecret + "\n"), SocketFlags.None, cancelToken);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "BYOC agent: relay handshake failed for team={Tid} challenge={Cid}",
                participationId, challengeId);
            return NotFound();
        }

        using var ws = await HttpContext.WebSockets.AcceptWebSocketAsync();
        logger.LogInformation("BYOC agent connected: team={Tid} challenge={Cid} relay={Ip}",
            participationId, challengeId, relayIpStr);
        await BridgeWebSocketToSocketAsync(ws, socket, cancelToken);
        return new EmptyResult();
    }

    /// <summary>
    /// Pump bytes bidirectionally between a WebSocket and a TCP socket until
    /// either side closes. Used to bridge a BYOC agent's WebSocket to its relay's
    /// control port — neither side's framing is interpreted, the bytes are the
    /// yamux session.
    /// </summary>
    private static async Task BridgeWebSocketToSocketAsync(WebSocket ws, Socket socket, CancellationToken token)
    {
        const int bufferSize = 16 * 1024;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using var net = new NetworkStream(socket, ownsSocket: false);

        async Task WsToTcp()
        {
            var buf = ArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var msg = await ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);
                    if (msg.MessageType == WebSocketMessageType.Close)
                        break;
                    if (msg.Count > 0)
                        await net.WriteAsync(buf.AsMemory(0, msg.Count), cts.Token);
                }
            }
            catch { /* peer closed / cancelled */ }
            finally { ArrayPool<byte>.Shared.Return(buf); await cts.CancelAsync(); }
        }

        async Task TcpToWs()
        {
            var buf = ArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var n = await net.ReadAsync(buf, cts.Token);
                    if (n <= 0)
                        break;
                    await ws.SendAsync(new ArraySegment<byte>(buf, 0, n),
                        WebSocketMessageType.Binary, true, cts.Token);
                }
            }
            catch { /* peer closed / cancelled */ }
            finally { ArrayPool<byte>.Shared.Return(buf); await cts.CancelAsync(); }
        }

        var a = WsToTcp();
        var b = TcpToWs();
        await Task.WhenAny(a, b);
        await cts.CancelAsync();
        try { await Task.WhenAll(a, b); } catch { /* already logged/ignored */ }
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Self-hosted ("bring your own container") setup bundle for the calling team
    /// and a self-hosted challenge: a ready-to-run docker-compose.yml with the
    /// agent image, tunnel URL, and team-scoped token baked in. The team drops it
    /// next to their own service and runs <c>docker compose up</c> — one outbound
    /// connection joins their service to the game (no public IP / inbound rule).
    /// </summary>
    [HttpGet("Byoc/Setup/{challengeId:int}")]
    [RequireUser]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ByocSetup(int id, int challengeId, CancellationToken cancelToken)
    {
        var part = await ResolveUserParticipationAsync(id, cancelToken);
        if (part is null)
            return Unauthorized(new RequestResponse(
                "not an accepted member of this game", StatusCodes.Status401Unauthorized));

        var chal = await db.GameChallenges
            .Where(c => c.Id == challengeId && c.GameId == id && c.AdSelfHosted && c.IsEnabled)
            .Select(c => new { c.Title, c.ExposePort, c.ContainerImage })
            .FirstOrDefaultAsync(cancelToken);
        if (chal is null)
            return NotFound(new RequestResponse("no such self-hosted challenge in this game"));

        var key = configService.GetXorKey();
        var svcPort = chal.ExposePort ?? 80;
        var wsScheme = Request.IsHttps ? "wss" : "ws";
        var httpScheme = Request.IsHttps ? "https" : "http";
        var tunnelUrl = $"{wsScheme}://{Request.Host}/api/Game/{id}/Ad/Byoc/Agent/{part.Id}/{challengeId}/" +
            AdTokenUtils.ByocAgentToken(part.Id, challengeId, key);
        var agentImage = configuration["Ad:Byoc:AgentImage"];
        if (string.IsNullOrWhiteSpace(agentImage))
            agentImage = AdContainerManager.ByocRelayImage;

        // If the challenge ships a service image, hand the team a one-command
        // setup.sh that pulls THAT real image from us and runs it — no placeholder,
        // no build. If there's no image (pure bring-your-own), fall back to the
        // out-of-the-box compose with a placeholder service.
        if (!string.IsNullOrWhiteSpace(chal.ContainerImage))
        {
            var imageUrl = $"{httpScheme}://{Request.Host}/api/Game/{id}/Ad/Byoc/Image/{part.Id}/{challengeId}/" +
                AdTokenUtils.ByocImageToken(part.Id, challengeId, key);
            var script = BuildByocSetupScript(
                id, challengeId, chal.Title, chal.ContainerImage, svcPort, imageUrl, tunnelUrl, agentImage);
            return File(System.Text.Encoding.UTF8.GetBytes(script), "application/x-sh",
                $"setup-{Slugify(chal.Title, challengeId)}.sh");
        }

        var compose = BuildByocCompose(id, challengeId, chal.Title, svcPort, tunnelUrl, agentImage);
        return File(System.Text.Encoding.UTF8.GetBytes(compose), "application/yaml",
            $"docker-compose-{Slugify(chal.Title, challengeId)}.yml");
    }

    /// <summary>
    /// Stream the challenge's real service image (a <c>docker save</c> tarball) to
    /// the team's setup script, so they <c>docker load</c> the exact vulnerable
    /// service instead of building one. Token-authed (no flags are in the image —
    /// they're delivered at runtime), validated BEFORE the export starts.
    /// </summary>
    [HttpGet("Byoc/Image/{participationId:int}/{challengeId:int}/{token}")]
    [AllowAnonymous]
    [EnableRateLimiting(nameof(RateLimiter.LimitPolicy.Concurrency))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ByocImage(int id, int participationId, int challengeId, string token,
        CancellationToken cancelToken)
    {
        var expected = AdTokenUtils.Hash($"adbyocimage:{participationId}:{challengeId}", configService.GetXorKey());
        byte[] presented;
        try { presented = Convert.FromHexString(token); }
        catch { return Unauthorized(); }
        if (!CryptographicOperations.FixedTimeEquals(expected, presented))
            return Unauthorized();

        var partInGame = await db.Participations.AnyAsync(p => p.Id == participationId && p.GameId == id, cancelToken);
        if (!partInGame)
            return NotFound();

        var image = await db.GameChallenges
            .Where(c => c.Id == challengeId && c.GameId == id && c.AdSelfHosted && c.IsEnabled)
            .Select(c => c.ContainerImage)
            .FirstOrDefaultAsync(cancelToken);
        if (string.IsNullOrWhiteSpace(image))
            return NotFound();

        var path = await adContainerManager.GetChallengeImageTarballAsync(image, cancelToken);
        if (path is null)
            return NotFound(new RequestResponse("service image is not available for download"));
        return PhysicalFile(path, "application/x-tar", "service-image.tar", enableRangeProcessing: true);
    }

    /// <summary>
    /// The bring-your-own-service compose (placeholder service that works out of
    /// the box). For teams who want to run their OWN modified service rather than
    /// the image we ship.
    /// </summary>
    [HttpGet("Byoc/Compose/{challengeId:int}")]
    [RequireUser]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ByocComposeBringYourOwn(int id, int challengeId, CancellationToken cancelToken)
    {
        var part = await ResolveUserParticipationAsync(id, cancelToken);
        if (part is null)
            return Unauthorized(new RequestResponse(
                "not an accepted member of this game", StatusCodes.Status401Unauthorized));

        var chal = await db.GameChallenges
            .Where(c => c.Id == challengeId && c.GameId == id && c.AdSelfHosted && c.IsEnabled)
            .Select(c => new { c.Title, c.ExposePort })
            .FirstOrDefaultAsync(cancelToken);
        if (chal is null)
            return NotFound(new RequestResponse("no such self-hosted challenge in this game"));

        var svcPort = chal.ExposePort ?? 80;
        var wsScheme = Request.IsHttps ? "wss" : "ws";
        var tunnelUrl = $"{wsScheme}://{Request.Host}/api/Game/{id}/Ad/Byoc/Agent/{part.Id}/{challengeId}/" +
            AdTokenUtils.ByocAgentToken(part.Id, challengeId, configService.GetXorKey());
        var agentImage = configuration["Ad:Byoc:AgentImage"];
        if (string.IsNullOrWhiteSpace(agentImage))
            agentImage = AdContainerManager.ByocRelayImage;

        var compose = BuildByocCompose(id, challengeId, chal.Title, svcPort, tunnelUrl, agentImage);
        return File(System.Text.Encoding.UTF8.GetBytes(compose), "application/yaml",
            $"docker-compose-{Slugify(chal.Title, challengeId)}.yml");
    }

    /// <summary>
    /// A filesystem-safe, unique-per-challenge slug from the title + id, so a team
    /// doing several BYOC challenges gets distinct download filenames (one doesn't
    /// overwrite another) — e.g. "pwn-armory-272".
    /// </summary>
    private static string Slugify(string title, int challengeId)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in title.ToLowerInvariant())
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
                sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(slug) ? $"challenge-{challengeId}" : $"{slug}-{challengeId}";
    }

    /// <summary>
    /// One-command installer: pull the real service image from us, write the
    /// compose (service + tunnel agent, flag delivered to GZCTF_FLAG_FILE), and
    /// start. No placeholder, no build — the team gets the exact vulnerable service.
    /// </summary>
    private static string BuildByocSetupScript(int gameId, int challengeId, string title, string containerImage,
        int svcPort, string imageUrl, string tunnelUrl, string agentImage)
    {
        var safeTitle = title.Replace('\n', ' ').Replace('\r', ' ').Replace("'", "");
        return string.Join('\n', new[]
        {
            "#!/bin/sh",
            $"# GZCTF Attack & Defense — self-hosted setup for \"{safeTitle}\"",
            "# Run it:  sh setup.sh        (needs docker + docker compose)",
            "set -e",
            $"DIR=\"gzctf-byoc-{gameId}-{challengeId}\"",
            "mkdir -p \"$DIR\" && cd \"$DIR\"",
            "echo '[1/3] Downloading your service image from the game server...'",
            $"curl -fSL \"{imageUrl}\" | docker load",
            "echo '[2/3] Writing docker-compose.yml...'",
            "cat > docker-compose.yml <<'COMPOSE'",
            $"name: gzctf-byoc-{gameId}-{challengeId}",
            "services:",
            "  # The real vulnerable service (just downloaded). Patch it to defend;",
            "  # it reads its rotating flag from GZCTF_FLAG_FILE (we deliver it there).",
            "  service:",
            $"    image: {containerImage}",
            "    restart: unless-stopped",
            "    environment:",
            "      GZCTF_FLAG_FILE: /shared/flag",
            "    volumes:",
            "      - flag:/shared:ro",
            "  # The tunnel agent — public image, token baked in. Don't edit.",
            "  gzctf-agent:",
            $"    image: {agentImage}",
            "    restart: unless-stopped",
            "    environment:",
            "      GZCTF_BYOC_MODE: agent",
            $"      GZCTF_BYOC_TUNNEL_URL: \"{tunnelUrl}\"",
            $"      GZCTF_BYOC_SERVICE: \"service:{svcPort}\"",
            "      GZCTF_BYOC_FLAG_FILE: /shared/flag",
            "    volumes:",
            "      - flag:/shared",
            "    depends_on:",
            "      - service",
            "volumes:",
            "  flag:",
            "COMPOSE",
            "echo '[3/3] Starting...'",
            "docker compose up -d",
            "echo 'Done — your service is running and connected. Watch the platform; your status should go green within a tick.'",
            ""
        });
    }

    /// <summary>
    /// Render the team-facing docker-compose for a BYOC challenge. It runs
    /// out-of-the-box: a placeholder service serves the rotating flag so the very
    /// first `docker compose up` goes green, and the team then swaps in their own
    /// vulnerable service. The agent image is public (Docker Hub), so nothing is
    /// built or configured — one click.
    /// </summary>
    private static string BuildByocCompose(int gameId, int challengeId, string title, int svcPort,
        string tunnelUrl, string agentImage)
    {
        var safeTitle = title.Replace('\n', ' ').Replace('\r', ' ');
        return string.Join('\n', new[]
        {
            $"# GZCTF Attack & Defense — self-hosted service for \"{safeTitle}\"",
            "# Unique per challenge, so you can run several BYOC challenges side by side.",
            $"name: gzctf-byoc-{gameId}-{challengeId}",
            "#",
            "#   docker compose up -d        # that's it — works out of the box.",
            "#",
            "# This runs immediately: the gzctf-agent makes ONE outbound connection to",
            "# the game (no public IP / inbound firewall / VPN), and the placeholder",
            "# 'service' serves the rotating flag so your status goes GREEN right away.",
            "# Then replace the 'service' block with your real vulnerable service — it",
            $"# only has to listen on port {svcPort} and read its flag from /shared/flag.",
            "services:",
            "  # ───────────────────────────────────────────────────────────────────",
            "  # >>> REPLACE THIS with your service (build: ./yourdir  OR  image: you/img).",
            "  #     Keep the flag volume; your service must listen on the port below.",
            "  # The default just serves /shared/flag so the connection works on day one.",
            "  service:",
            "    image: alpine/socat",
            $"    command: [\"TCP-LISTEN:{svcPort},fork,reuseaddr\", \"SYSTEM:cat /shared/flag 2>/dev/null\"]",
            "    restart: unless-stopped",
            "    volumes:",
            "      - flag:/shared:ro        # rotating flag at /shared/flag (read-only to you)",
            "",
            "  # The tunnel agent — public image, token baked in. Don't edit this.",
            "  gzctf-agent:",
            $"    image: {agentImage}",
            "    restart: unless-stopped",
            "    environment:",
            "      GZCTF_BYOC_MODE: agent",
            $"      GZCTF_BYOC_TUNNEL_URL: \"{tunnelUrl}\"",
            $"      GZCTF_BYOC_SERVICE: \"service:{svcPort}\"",
            "      GZCTF_BYOC_FLAG_FILE: /shared/flag",
            "    volumes:",
            "      - flag:/shared",
            "    depends_on:",
            "      - service",
            "",
            "volumes:",
            "  flag:",
            ""
        });
    }

    private async Task<AdSubmitResultModel> ProcessSingleFlagAsync(
        Participation attackerPart, AdRound currentRound, string raw, CancellationToken token)
    {
        var submittedFlag = (raw ?? string.Empty).Trim();
        var result = new AdSubmitResultModel { Flag = submittedFlag };

        if (string.IsNullOrEmpty(submittedFlag))
        {
            result.Status = "wrong";
            result.Message = "empty flag";
            return result;
        }

        var adFlag = await db.AdFlags
            .Include(f => f.AdTeamService)
            .FirstOrDefaultAsync(f => f.Flag == submittedFlag, token);

        if (adFlag is null)
        {
            result.Status = "wrong";
            result.Message = "flag not recognized";
            return result;
        }

        var victimPart = await db.Participations
            .FirstOrDefaultAsync(p => p.Id == adFlag.AdTeamService.ParticipationId, token);
        if (victimPart is null || victimPart.GameId != attackerPart.GameId)
        {
            result.Status = "wrong";
            result.Message = "flag from another game";
            return result;
        }

        if (victimPart.Id == attackerPart.Id)
        {
            result.Status = "self_attack";
            result.Message = "cannot submit your own flag";
            return result;
        }

        var lifetimeTicks = await db.Games
            .Where(g => g.Id == currentRound.GameId)
            .Select(g => g.AdFlagLifetimeTicks)
            .FirstOrDefaultAsync(token) ?? 5;
        if (adFlag.PlantedAtRound < currentRound.Number - lifetimeTicks + 1)
        {
            result.Status = "expired";
            result.FlagPlantedAtRound = adFlag.PlantedAtRound;
            result.Message = $"flag was planted {currentRound.Number - adFlag.PlantedAtRound} ticks ago (lifetime {lifetimeTicks})";
            return result;
        }

        var alreadySubmitted = await db.AdAttacks.AnyAsync(
            a => a.AttackerParticipationId == attackerPart.Id && a.AdFlagId == adFlag.Id, token);
        if (alreadySubmitted)
        {
            result.Status = "duplicate";
            result.FlagPlantedAtRound = adFlag.PlantedAtRound;
            result.Message = "you've already submitted this flag";
            return result;
        }

        var attack = new AdAttack
        {
            AdFlagId = adFlag.Id,
            AttackerParticipationId = attackerPart.Id,
            VictimParticipationId = victimPart.Id,
            ChallengeId = adFlag.AdTeamService.ChallengeId,
            SubmittedAtRound = currentRound.Number,
            Points = 0 // assigned below from the stable capture order
        };

        // Serialize capturers of the SAME flag with a per-flag advisory lock held
        // for the insert+count. First-blood weighting counts prior captures
        // (a.Id < this.Id); under READ COMMITTED two DIFFERENT teams submitting the
        // same flag concurrently would each see 0 committed priors and both bank
        // full first-blood (distinct Ids do NOT imply distinct visible counts). The
        // lock makes the count reflect a consistent committed set; it's released on
        // commit/rollback.
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext('gzctf_ad_capture'), {adFlag.Id})", token);

        try
        {
            await db.AdAttacks.AddAsync(attack, token);
            await db.SaveChangesAsync(token); // assigns attack.Id
        }
        catch (DbUpdateException)
        {
            // Lost the unique (attacker, flag) race. Detach the failed insert so
            // it doesn't stay tracked in Added state — otherwise the NEXT flag's
            // SaveChanges in this shared per-request context retries this poisoned
            // row and fails too, silently dropping every later valid capture in
            // the batch (mirrors the VPN-peer detach pattern below).
            db.Entry(attack).State = EntityState.Detached;
            await tx.RollbackAsync(token);
            result.Status = "duplicate";
            result.FlagPlantedAtRound = adFlag.PlantedAtRound;
            result.Message = "already submitted";
            return result;
        }

        // First-blood weighting from the now-consistent committed capture order
        // (the advisory lock above serializes concurrent same-flag capturers, so
        // each gets a distinct prior-count → distinct rank).
        var priorCapturers = await db.AdAttacks.CountAsync(
            a => a.AdFlagId == adFlag.Id && a.Id < attack.Id, token);
        // Provisional share for immediate feedback only. The authoritative attack
        // score is recomputed at scoreboard render from the flag's FINAL capturer
        // count (AttackPool/k), which isn't known yet — later teams may also steal
        // this flag. Right now the caller is the (priorCapturers+1)-th capturer, so
        // this is the upper bound on their final share; it shrinks as more teams
        // capture the same flag.
        var points = AdScoring.AttackShare(priorCapturers + 1);
        attack.Points = points;
        await db.SaveChangesAsync(token);
        await tx.CommitAsync(token);

        logger.SystemLog(
            $"A&D attack landed: attacker={attackerPart.Id} victim={victimPart.Id} chal={adFlag.AdTeamService.ChallengeId} round={currentRound.Number} pts={points:F2}",
            TaskStatus.Success, LogLevel.Information);

        // Light up the public attack page: a capture fires a projectile from
        // the attacker's node at the *victim* team's node (not the center HQ).
        await BroadcastAdAttackAsync(attackerPart.GameId, attackerPart.Id, victimPart.Id,
            adFlag.AdTeamService.ChallengeId, token);

        result.Status = "accepted";
        result.Points = points;
        result.FlagPlantedAtRound = adFlag.PlantedAtRound;
        return result;
    }

    /// <summary>
    /// Broadcast an accepted A&amp;D capture to the public attack page. The
    /// event carries both the attacker and the <see cref="AttackEvent.VictimTeamName"/>
    /// so the visualization can fly the projectile team→team instead of into
    /// the center HQ. Best-effort: a broadcast failure never fails the submit.
    /// </summary>
    private async Task BroadcastAdAttackAsync(int gameId, int attackerPartId, int victimPartId,
        int challengeId, CancellationToken token)
    {
        try
        {
            // Gate the public attack feed: never broadcast for Hidden games, and
            // suppress during the freeze window [FreezeTimeUtc, EndTimeUtc) so the
            // unauth'd AttackHub can't reveal late-game A&D captures the frozen
            // scoreboard hides. Mirrors SubmissionRepository.SendAttackEventInternal.
            var gate = await db.Games
                .Where(g => g.Id == gameId)
                .Select(g => new { g.Hidden, g.FreezeTimeUtc, g.EndTimeUtc })
                .FirstOrDefaultAsync(token);
            if (gate is null || gate.Hidden)
                return;
            var nowUtc = DateTimeOffset.UtcNow;
            if (gate.FreezeTimeUtc is { } freeze && nowUtc >= freeze && nowUtc < gate.EndTimeUtc)
                return;

            var teams = await db.Participations
                .Where(p => p.Id == attackerPartId || p.Id == victimPartId)
                .Select(p => new { p.Id, p.Team.Name, p.Team.AvatarHash })
                .ToListAsync(token);
            var attacker = teams.FirstOrDefault(t => t.Id == attackerPartId);
            var victim = teams.FirstOrDefault(t => t.Id == victimPartId);

            var chal = await db.GameChallenges
                .Where(c => c.Id == challengeId)
                .Select(c => new { c.Title, c.Category })
                .FirstOrDefaultAsync(token);

            // First blood = the first time this (victim, challenge) is breached
            // all game — fires the dramatic laser at the victim's node, once per
            // defending team. The attack row is already saved, so a count of 1
            // means this capture is that first breach. Everything after is a
            // plain Normal burst (no per-tick laser spam).
            var breaches = await db.AdAttacks
                .CountAsync(a => a.VictimParticipationId == victimPartId && a.ChallengeId == challengeId, token);
            var type = breaches <= 1 ? SubmissionType.FirstBlood : SubmissionType.Normal;

            var evt = new AttackEvent(
                attacker?.Name ?? string.Empty,
                attacker?.AvatarHash is null ? null : $"/assets/{attacker.AvatarHash}/avatar",
                null,
                chal?.Title ?? string.Empty,
                chal?.Category ?? ChallengeCategory.Misc,
                type,
                DateTimeOffset.UtcNow,
                victim?.Name);

            await attackHub.Clients.Group($"AttackGame_{gameId}").ReceivedAttack(evt);
            attackStream.PublishAttack(gameId, evt);
        }
        catch (Exception e)
        {
            logger.LogErrorMessage(e, "Failed to broadcast A&D attack event");
        }
    }

    private async Task<Participation?> ResolveTeamApiTokenAsync(int gameId, CancellationToken token)
    {
        var auth = Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!auth.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return null;

        var presented = auth[scheme.Length..].Trim();
        if (!presented.StartsWith(AdTokenUtils.TokenPrefix, StringComparison.Ordinal))
            return null;

        var hash = AdTokenUtils.Hash(presented, configService.GetXorKey());

        var row = await db.AdTeamApiTokens
            .Include(t => t.Participation).ThenInclude(p => p.Members)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, token);

        if (row is null
            || row.Participation.GameId != gameId
            || row.Participation.Status != ParticipationStatus.Accepted)
            return null;

        // Verify the user this token belongs to is *still* on the team. This
        // is what makes member-kick an instant revocation: even if the kicked
        // member kept their token, their UserParticipation row was removed by
        // the kick flow and this lookup fails.
        var stillAMember = row.Participation.Members.Any(m => m.UserId == row.UserId);
        if (!stillAMember) return null;

        // Throttle the LastUsedAt write (mirrors the LastVisitedUtc throttle in
        // RequirePrivilegeAttribute): Targets/Koth-token are polled in tight loops,
        // and an unconditional UPDATE+SaveChanges per request hammers one hot row.
        var nowUtc = DateTimeOffset.UtcNow;
        if (row.LastUsedAt is null || nowUtc - row.LastUsedAt.Value > TimeSpan.FromSeconds(30))
        {
            row.LastUsedAt = nowUtc;
            await db.SaveChangesAsync(token);
        }
        return row.Participation;
    }

    private async Task<Participation?> ResolveUserParticipationAsync(int gameId, CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return null;

        return await db.Participations
            .FirstOrDefaultAsync(p => p.GameId == gameId
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);
    }

    /// <summary>
    /// King of the Hill — the caller's control token, the ID-FREE form. Write this exact
    /// value into a hill's <c>/koth/king</c> marker to claim control. The token is
    /// GAME-WIDE — the SAME value works on EVERY hill in this game — and stable for a
    /// whole refresh window (it rotates only when the hills reset, every
    /// <c>KothRefreshTicks</c> ticks), so fetch it once after a reset and plant it on
    /// whichever hills you capture. Prefer this over the per-challenge variant — you
    /// never need a challenge id. Accepts the same auth as Submit (<c>Bearer ad_...</c>
    /// for scripted play, or the session cookie).
    /// </summary>
    [HttpGet("Koth/Token")]
    [ProducesResponseType(typeof(KothTokenModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> KothTokenAny(int id, CancellationToken token)
    {
        var part = await ResolveTeamApiTokenAsync(id, token) ?? await ResolveUserParticipationAsync(id, token);
        if (part is null)
            return Unauthorized(new RequestResponse("not an accepted member of this game", StatusCodes.Status401Unauthorized));

        var hasKoth = await db.GameChallenges.AnyAsync(
            c => c.GameId == id && c.Type == ChallengeType.KingOfTheHill && c.IsEnabled, token);
        if (!hasKoth)
            return NotFound(new RequestResponse("no King of the Hill challenge in this game"));

        return Ok(await ResolveKothTokenAsync(id, part.Id, token));
    }

    /// <summary>
    /// King of the Hill — the caller's control token, scoped via a specific hill's id.
    /// Identical value to the ID-free <see cref="KothTokenAny"/> (the token is GAME-WIDE);
    /// the <c>{challengeId}</c> segment only validates the hill exists and is otherwise
    /// ignored. Kept for callers that already have a challenge id. Same auth as Submit.
    /// </summary>
    [HttpGet("Koth/{challengeId:int}/Token")]
    [ProducesResponseType(typeof(KothTokenModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> KothToken(int id, int challengeId, CancellationToken token)
    {
        var part = await ResolveTeamApiTokenAsync(id, token) ?? await ResolveUserParticipationAsync(id, token);
        if (part is null)
            return Unauthorized(new RequestResponse("not an accepted member of this game", StatusCodes.Status401Unauthorized));

        var isKoth = await db.GameChallenges.AnyAsync(
            c => c.Id == challengeId && c.GameId == id
                 && c.Type == ChallengeType.KingOfTheHill && c.IsEnabled, token);
        if (!isKoth)
            return NotFound(new RequestResponse("not a King of the Hill challenge in this game"));

        return Ok(await ResolveKothTokenAsync(id, part.Id, token));
    }

    /// <summary>
    /// Resolve a team's current game-wide KotH control token. The token is minted once
    /// per refresh window at the window anchor round and is stable across the window —
    /// resolve it by the anchor (not the current round) and by participation only (NOT
    /// by challenge; one row serves every hill in the game). Shared by the ID-free and
    /// per-challenge token endpoints.
    /// </summary>
    private async Task<KothTokenModel> ResolveKothTokenAsync(int gameId, int participationId, CancellationToken token)
    {
        var latestRound = await db.AdRounds
            .Where(r => r.GameId == gameId)
            .OrderByDescending(r => r.Number)
            .Select(r => r.Number)
            .FirstOrDefaultAsync(token);

        if (latestRound == 0)
            return new KothTokenModel { Round = 0, Token = null, Status = "warmup" };

        var refreshTicks = Math.Max(1, await db.Games
            .Where(g => g.Id == gameId)
            .Select(g => g.KothRefreshTicks)
            .FirstOrDefaultAsync(token) ?? 5);
        var anchorRound = KothWindow.AnchorRound(latestRound, refreshTicks);

        var tok = await db.KothTokens
            .Where(k => k.ParticipationId == participationId && k.RoundNumber == anchorRound)
            .Select(k => k.Token)
            .FirstOrDefaultAsync(token);

        return new KothTokenModel
        {
            // Window-anchor round (the token's round) — stable across the window.
            Round = anchorRound,
            Token = tok,
            // Distinguish "missed the mint this window" from "token here, plant it".
            Status = tok is null ? "no-token-this-round" : "ready"
        };
    }

    /// <summary>
    /// King of the Hill — current hill state for the caller's team. Lets a player
    /// confirm a plant took effect without polling the scoreboard (which only
    /// updates once per tick). Returns the round being checked, who the platform
    /// currently records as the holder, and the last functional verdict on the
    /// hill itself. Auth: same dual-auth as Submit / Token.
    /// </summary>
    [HttpGet("Koth/{challengeId:int}/State")]
    [ProducesResponseType(typeof(KothHillStateModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> KothState(int id, int challengeId, CancellationToken token)
    {
        var part = await ResolveTeamApiTokenAsync(id, token) ?? await ResolveUserParticipationAsync(id, token);
        if (part is null)
            return Unauthorized(new RequestResponse("not an accepted member of this game", StatusCodes.Status401Unauthorized));

        var isKoth = await db.GameChallenges.AnyAsync(
            c => c.Id == challengeId && c.GameId == id
                 && c.Type == ChallengeType.KingOfTheHill && c.IsEnabled, token);
        if (!isKoth)
            return NotFound(new RequestResponse("not a King of the Hill challenge in this game"));

        var latest = await db.KothControlResults
            .Where(r => r.ChallengeId == challengeId)
            .OrderByDescending(r => r.AdRound.Number)
            .Select(r => new
            {
                Round = r.AdRound.Number,
                r.ControllingParticipationId,
                HolderName = r.ControllingParticipation != null ? r.ControllingParticipation.Team.Name : null,
                Status = (AdCheckStatus?)r.Status,
                r.CheckedAt
            })
            .FirstOrDefaultAsync(token);

        var lastRefresh = await db.KothTargets
            .Where(t => t.GameId == id && t.ChallengeId == challengeId)
            .Select(t => (int?)t.LastRefreshRound)
            .FirstOrDefaultAsync(token) ?? 0;

        return Ok(new KothHillStateModel
        {
            ChallengeId = challengeId,
            Round = latest?.Round ?? 0,
            HolderParticipationId = latest?.ControllingParticipationId,
            HolderTeamName = latest?.HolderName,
            IsYou = latest?.ControllingParticipationId == part.Id,
            Status = latest?.Status?.ToString(),
            CheckedAt = latest?.CheckedAt,
            LastRefreshRound = lastRefresh
        });
    }

    /// <summary>
    /// King of the Hill — current state of EVERY hill in the game in one call, so a
    /// player can see all hills at once (name, target IP:port, who holds it, functional
    /// status) without having to know or pass individual challenge ids. This is the
    /// list form of <see cref="KothState"/> — the toolkit's "did my plant take?" view.
    /// Ordered by challenge id for a stable list. Auth: same dual-auth as Submit.
    /// </summary>
    [HttpGet("Koth/Hills")]
    [ProducesResponseType(typeof(List<KothHillStateModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> KothHills(int id, CancellationToken token)
    {
        var part = await ResolveTeamApiTokenAsync(id, token) ?? await ResolveUserParticipationAsync(id, token);
        if (part is null)
            return Unauthorized(new RequestResponse("not an accepted member of this game", StatusCodes.Status401Unauthorized));

        var hills = await db.GameChallenges
            .Where(c => c.GameId == id && c.Type == ChallengeType.KingOfTheHill && c.IsEnabled)
            .OrderBy(c => c.Id)
            .Select(c => new { c.Id, c.Title })
            .ToListAsync(token);
        if (hills.Count == 0)
            return Ok(new List<KothHillStateModel>());

        var hillIds = hills.Select(h => h.Id).ToList();

        // Latest control verdict per hill (holder + functional status + round).
        // Flat "no newer row exists in this hill" anti-join rather than
        // GroupBy().First() — EF Core cannot translate a GroupBy whose grouped
        // element is re-projected through a navigation (it throws at runtime with
        // 'EmptyProjectionMember'). This mirrors the single-hill KothState
        // projection, just batched across every hill. The unique index on
        // (ChallengeId, AdRoundId) means one max-round row per hill; the in-memory
        // GroupBy below is purely defensive so a list view never 500s.
        var latestByChallenge = (await db.KothControlResults
            .Where(r => hillIds.Contains(r.ChallengeId)
                        && !db.KothControlResults.Any(r2 =>
                            r2.ChallengeId == r.ChallengeId && r2.AdRound.Number > r.AdRound.Number))
            .Select(r => new
            {
                r.ChallengeId,
                Round = r.AdRound.Number,
                r.ControllingParticipationId,
                HolderName = r.ControllingParticipation != null ? r.ControllingParticipation.Team.Name : null,
                Status = (AdCheckStatus?)r.Status,
                r.CheckedAt
            })
            .ToListAsync(token))
            .GroupBy(x => x.ChallengeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Round).First());

        // Current container IP:port + last refresh round per hill.
        var targetByChallenge = (await db.KothTargets
            .Where(t => t.GameId == id && hillIds.Contains(t.ChallengeId))
            .Select(t => new
            {
                t.ChallengeId,
                Ip = t.Container != null ? t.Container.IP : null,
                Port = t.Container != null ? t.Container.Port : (int?)null,
                t.LastRefreshRound
            })
            .ToListAsync(token))
            .GroupBy(x => x.ChallengeId)
            .ToDictionary(g => g.Key, g => g.First());

        var list = hills.Select(h =>
        {
            latestByChallenge.TryGetValue(h.Id, out var v);
            targetByChallenge.TryGetValue(h.Id, out var tgt);
            return new KothHillStateModel
            {
                ChallengeId = h.Id,
                Title = h.Title,
                Round = v?.Round ?? 0,
                HolderParticipationId = v?.ControllingParticipationId,
                HolderTeamName = v?.HolderName,
                IsYou = v?.ControllingParticipationId == part.Id,
                Status = v?.Status?.ToString(),
                CheckedAt = v?.CheckedAt,
                LastRefreshRound = tgt?.LastRefreshRound ?? 0,
                Ip = tgt?.Ip,
                Port = tgt?.Port
            };
        }).ToList();

        return Ok(list);
    }

    /// <summary>
    /// Generate or rotate the caller's own A&amp;D API token for this game.
    /// Any team member can manage their own token — no captain check.
    /// Returns the plaintext token exactly once.
    /// </summary>
    [RequireUser]
    [HttpPost("Token")]
    [ProducesResponseType(typeof(AdTokenGenerateResultModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> RotateToken(int id, CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var participation = await db.Participations
            .FirstOrDefaultAsync(p => p.GameId == id
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);

        if (participation is null) return Forbid();

        var plaintext = AdTokenUtils.GeneratePlaintext();
        var hint = AdTokenUtils.BuildHint(plaintext);
        var hash = AdTokenUtils.Hash(plaintext, configService.GetXorKey());
        var now = DateTimeOffset.UtcNow;

        var existing = await db.AdTeamApiTokens
            .FirstOrDefaultAsync(t => t.UserId == user.Id && t.ParticipationId == participation.Id, token);

        if (existing is null)
        {
            existing = new AdTeamApiToken
            {
                UserId = user.Id,
                ParticipationId = participation.Id,
                TokenHash = hash,
                Hint = hint,
                CreatedAt = now,
                LastRotatedAt = now
            };
            await db.AdTeamApiTokens.AddAsync(existing, token);
        }
        else
        {
            existing.TokenHash = hash;
            existing.Hint = hint;
            existing.LastRotatedAt = now;
            existing.LastUsedAt = null;
        }

        await db.SaveChangesAsync(token);

        logger.SystemLog(
            $"A&D user token rotated: user={user.Id} participation={participation.Id}",
            TaskStatus.Success, LogLevel.Information);

        return Ok(new AdTokenGenerateResultModel
        {
            Token = plaintext,
            Hint = hint,
            RotatedAt = now
        });
    }

    /// <summary>
    /// Read the hint for the caller's own A&amp;D API token. Per-user; no
    /// captain check needed (each user manages their own token). Never
    /// reveals the plaintext.
    /// </summary>
    [RequireUser]
    [HttpGet("Token")]
    [ProducesResponseType(typeof(AdTokenHintModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTokenHint(int id, CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var participation = await db.Participations
            .FirstOrDefaultAsync(p => p.GameId == id
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);

        if (participation is null) return Forbid();

        var existing = await db.AdTeamApiTokens
            .FirstOrDefaultAsync(t => t.UserId == user.Id && t.ParticipationId == participation.Id, token);

        if (existing is null)
            return Ok(new AdTokenHintModel { Exists = false, CanManage = true });

        return Ok(new AdTokenHintModel
        {
            Exists = true,
            Hint = existing.Hint,
            CreatedAt = existing.CreatedAt,
            LastRotatedAt = existing.LastRotatedAt,
            LastUsedAt = existing.LastUsedAt,
            CanManage = true
        });
    }

    /// <summary>
    /// Revoke the caller's own A&amp;D API token. Per-user — does not affect
    /// other team members' tokens. After this call, the caller's Bearer-token
    /// submissions stop working until they generate a new one.
    /// </summary>
    [RequireUser]
    [HttpDelete("Token")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RevokeToken(int id, CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var participation = await db.Participations
            .FirstOrDefaultAsync(p => p.GameId == id
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);

        if (participation is null) return Forbid();

        var existing = await db.AdTeamApiTokens
            .FirstOrDefaultAsync(t => t.UserId == user.Id && t.ParticipationId == participation.Id, token);

        if (existing is null)
            return NoContent();

        db.AdTeamApiTokens.Remove(existing);
        await db.SaveChangesAsync(token);

        logger.SystemLog(
            $"A&D user token revoked: user={user.Id} participation={participation.Id}",
            TaskStatus.Success, LogLevel.Information);

        return NoContent();
    }

    /// <summary>
    /// Self-reset one of your team's A&amp;D containers to baseline.
    /// </summary>
    [RequireUser]
    [HttpPost("Services/{adTeamServiceId:int}/Reset")]
    [EnableRateLimiting(nameof(RateLimiter.LimitPolicy.Concurrency))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ResetService(int id, int adTeamServiceId, CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var ts = await db.AdTeamServices
            .Include(t => t.Participation)
            .Include(t => t.Challenge)
            .FirstOrDefaultAsync(t => t.Id == adTeamServiceId, token);

        if (ts is null || ts.Participation.GameId != id)
            return NotFound();

        // Caller must be an ACCEPTED member of the team that owns this service — a
        // denied/suspended participation must not be able to reset containers.
        var isMember = await db.Participations
            .AnyAsync(p => p.Id == ts.ParticipationId
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);
        if (!isMember) return Forbid();

        if (!ts.Challenge.AdAllowSelfReset)
            return BadRequest(new RequestResponse("Self-reset is disabled for this challenge by the operator"));

        // Reset only inside the game window. A post-game reset would recreate a
        // container for a finished game and race the reconciler's end-of-game
        // teardown; a pre-start reset has nothing to reset.
        var gameWindow = await db.Games
            .Where(g => g.Id == id)
            .Select(g => new { g.StartTimeUtc, g.EndTimeUtc, g.AdResetCooldownMinutes })
            .FirstOrDefaultAsync(token);
        if (gameWindow is null)
            return NotFound();
        var nowReset = DateTimeOffset.UtcNow;
        if (nowReset < gameWindow.StartTimeUtc || nowReset > gameWindow.EndTimeUtc)
            return BadRequest(new RequestResponse("Reset is only available while the game is running"));

        // Cooldown check — game-wide policy.
        var cooldownMinutes = gameWindow.AdResetCooldownMinutes ?? 5;
        if (ts.LastResetAt is { } last)
        {
            var elapsed = DateTimeOffset.UtcNow - last;
            if (elapsed < TimeSpan.FromMinutes(cooldownMinutes))
            {
                var retryAfter = (int)(TimeSpan.FromMinutes(cooldownMinutes) - elapsed).TotalSeconds;
                Response.Headers.RetryAfter = retryAfter.ToString();
                return StatusCode(StatusCodes.Status429TooManyRequests,
                    new RequestResponse(
                        $"Cooldown active; try again in {retryAfter}s",
                        StatusCodes.Status429TooManyRequests));
            }
        }

        var ok = await adContainerManager.RestartContainerAsync(ts.Id, token);
        if (!ok)
            return BadRequest(new RequestResponse("Container restart failed; check logs"));

        logger.SystemLog($"A&D self-reset: team={ts.ParticipationId} challenge={ts.ChallengeId} by user={user.Id}",
            TaskStatus.Success, LogLevel.Information);

        return Ok();
    }

    /// <summary>
    /// Get the player view of A&amp;D state for their team in this game.
    /// </summary>
    [RequireUser]
    [HttpGet("State")]
    [ProducesResponseType(typeof(AdStateModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> State(int id, CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var part = await db.Participations
            .FirstOrDefaultAsync(p => p.GameId == id
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);

        if (part is null) return Forbid();

        var currentRound = await db.AdRounds
            .Where(r => r.GameId == id)
            .OrderByDescending(r => r.Number)
            .FirstOrDefaultAsync(token);

        var services = await db.AdTeamServices
            .Where(t => t.ParticipationId == part.Id)
            .Include(t => t.Challenge)
            .Include(t => t.Container)
            .ToListAsync(token);

        var serviceIds = services.Select(s => s.Id).ToList();
        var currentFlags = currentRound is null ? new Dictionary<int, string>() :
            await db.AdFlags
                .Where(f => f.AdRoundId == currentRound.Id && serviceIds.Contains(f.AdTeamServiceId))
                .ToDictionaryAsync(f => f.AdTeamServiceId, f => f.Flag, token);

        var lastChecks = serviceIds.Count == 0 ? [] :
            await db.AdCheckResults
                .Where(c => serviceIds.Contains(c.AdTeamServiceId))
                .GroupBy(c => c.AdTeamServiceId)
                .Select(g => g.OrderByDescending(c => c.CheckedAt).First())
                .ToListAsync(token);

        var lastChecksByService = lastChecks.ToDictionary(c => c.AdTeamServiceId);

        // Reset cooldown is game-wide policy — fetch once, apply to every service.
        // Also need the end time: the post-game snapshot must NOT be exposed to
        // players before the game ends (it's their box's committed state).
        var gameInfo = await db.Games
            .Where(g => g.Id == id)
            .Select(g => new { g.EndTimeUtc, g.AdResetCooldownMinutes })
            .FirstOrDefaultAsync(token);
        var cooldownMinutes = gameInfo?.AdResetCooldownMinutes ?? 5;
        var gameEnded = gameInfo is not null && DateTimeOffset.UtcNow >= gameInfo.EndTimeUtc;

        var serviceModels = services.Select(s =>
        {
            var cooldownRemaining = s.LastResetAt is { } last
                ? Math.Max(0, (int)(TimeSpan.FromMinutes(cooldownMinutes) - (DateTimeOffset.UtcNow - last)).TotalSeconds)
                : 0;

            return new AdTeamServiceStateModel
            {
                AdTeamServiceId = s.Id,
                ChallengeId = s.ChallengeId,
                ChallengeTitle = s.Challenge.Title,
                ContainerIp = s.Container?.IP,
                ContainerPort = s.Container?.Port,
                CurrentFlag = currentFlags.GetValueOrDefault(s.Id),
                LastCheckStatus = lastChecksByService.GetValueOrDefault(s.Id)?.Status.ToString(),
                LastResetAt = s.LastResetAt,
                CanReset = s.Challenge.AdAllowSelfReset && cooldownRemaining == 0,
                ResetCooldownSecondsRemaining = cooldownRemaining > 0 ? cooldownRemaining : null,
                // Post-game only: never surface the snapshot while the game runs.
                SnapshotAvailable = gameEnded && !string.IsNullOrEmpty(s.SnapshotBlobKey),
                SelfHosted = s.Challenge.AdSelfHosted
            };
        }).ToList();

        return Ok(new AdStateModel
        {
            CurrentRound = currentRound?.Number ?? 0,
            RoundStartedAt = currentRound?.StartedAt,
            RoundEndsAt = currentRound?.EndsAt,
            Services = serviceModels
        });
    }

    /// <summary>
    /// List every other team's container IP per enabled A&amp;D challenge,
    /// plus the last health-check verdict for each. Attack-first players need
    /// this to know where to aim — without it A&amp;D is unplayable. Caller's
    /// own team rows are excluded so the response is directly usable as a
    /// targets list. Empty teams[] until the warmup round has elapsed.
    /// </summary>
    /// <remarks>
    /// Dual auth (same shape as Submit): cookie session OR
    /// <c>Authorization: Bearer ad_...</c> token. Wired up so exploit
    /// scripts can poll without a browser.
    /// </remarks>
    [HttpGet("Targets")]
    [ProducesResponseType(typeof(AdTargetsModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> Targets(int id, CancellationToken token)
    {
        var caller = await ResolveTeamApiTokenAsync(id, token)
                     ?? await ResolveUserParticipationAsync(id, token);
        if (caller is null) return Unauthorized();

        var currentRound = await db.AdRounds
            .Where(r => r.GameId == id)
            .OrderByDescending(r => r.Number)
            .Select(r => r.Number)
            .FirstOrDefaultAsync(token);

        var enabledChallenges = await db.GameChallenges
            .Where(c => c.GameId == id && c.IsEnabled
                && (c.Type == ChallengeType.AttackDefense || c.Type == ChallengeType.KingOfTheHill))
            .OrderBy(c => c.Id)
            .ToListAsync(token);

        var result = new AdTargetsModel { CurrentRound = currentRound };
        if (currentRound == 0)
            return Ok(result); // warmup — no targets yet

        // KotH hills — single shared container per challenge. Loaded up-front
        // so each enabledChallenges entry can stamp Hill in the loop below.
        // We need the latest functional verdict per hill too, sourced from
        // KothControlResults (the equivalent of AdCheckResults for hills).
        var kothChallengeIds = enabledChallenges
            .Where(c => c.Type == ChallengeType.KingOfTheHill).Select(c => c.Id).ToList();
        var hillByChallenge = kothChallengeIds.Count == 0
            ? new Dictionary<int, (string? Ip, int? Port, int LastRefreshRound)>()
            : (await db.KothTargets
                .Where(t => t.GameId == id && kothChallengeIds.Contains(t.ChallengeId))
                .Include(t => t.Container)
                .Select(t => new
                {
                    t.ChallengeId,
                    Ip = t.Container != null ? t.Container.IP : null,
                    Port = t.Container != null ? t.Container.Port : (int?)null,
                    t.LastRefreshRound
                })
                .ToListAsync(token))
                .ToDictionary(t => t.ChallengeId, t => (t.Ip, t.Port, t.LastRefreshRound));
        // Latest functional verdict per hill. Flat "no newer row exists for this
        // hill" anti-join, NOT GroupBy().OrderByDescending().First() — EF Core
        // cannot translate a grouped element re-projected through a navigation
        // (r.AdRound.Number) and throws 'EmptyProjectionMember' at runtime,
        // which would 500 the whole /Targets view whenever the game has any KotH
        // challenge. Same fix already applied to Koth/Hills (commit 37164f50);
        // the in-memory GroupBy is defensive against an unexpected duplicate.
        var hillStatusByChallenge = kothChallengeIds.Count == 0
            ? new Dictionary<int, AdCheckStatus?>()
            : (await db.KothControlResults
                .Where(r => kothChallengeIds.Contains(r.ChallengeId)
                            && !db.KothControlResults.Any(r2 =>
                                r2.ChallengeId == r.ChallengeId && r2.AdRound.Number > r.AdRound.Number))
                .Select(r => new { r.ChallengeId, Status = (AdCheckStatus?)r.Status })
                .ToListAsync(token))
                .GroupBy(x => x.ChallengeId)
                .ToDictionary(g => g.Key, g => g.First().Status);

        var services = await db.AdTeamServices
            .Where(ts => ts.Participation.GameId == id
                && ts.ParticipationId != caller.Id
                && ts.Container != null
                && ts.Participation.Status == ParticipationStatus.Accepted)
            .Include(ts => ts.Container)
            .Include(ts => ts.Participation).ThenInclude(p => p.Team)
            .Include(ts => ts.Participation).ThenInclude(p => p.Division)
            .ToListAsync(token);

        var serviceIds = services.Select(s => s.Id).ToList();
        var lastChecks = serviceIds.Count == 0 ? [] :
            await db.AdCheckResults
                .Where(c => serviceIds.Contains(c.AdTeamServiceId))
                .GroupBy(c => c.AdTeamServiceId)
                .Select(g => g.OrderByDescending(c => c.CheckedAt).First())
                .ToListAsync(token);
        var lastChecksByService = lastChecks.ToDictionary(c => c.AdTeamServiceId);

        // Tick is game-wide — one value for every challenge row below.
        var tickSeconds = await db.Games
            .Where(g => g.Id == id)
            .Select(g => g.AdTickSeconds)
            .FirstOrDefaultAsync(token) ?? 60;

        foreach (var chal in enabledChallenges)
        {
            var row = new AdChallengeTargets
            {
                ChallengeId = chal.Id,
                Title = chal.Title,
                TickSeconds = tickSeconds,
                Teams = chal.Type == ChallengeType.KingOfTheHill
                    ? [] // KotH is shared — no per-team targets
                    : services
                        .Where(s => s.ChallengeId == chal.Id)
                        .OrderBy(s => s.Participation.Team.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(s => new AdTeamTarget
                        {
                            ParticipationId = s.ParticipationId,
                            TeamName = s.Participation.Team.Name,
                            Division = s.Participation.Division?.Name,
                            Ip = s.Container?.IP,
                            Port = s.Container?.Port,
                            LastCheckStatus = lastChecksByService.GetValueOrDefault(s.Id)?.Status.ToString(),
                        })
                        .ToList()
            };

            // Surface the shared hill so players can target it after the 5-tick
            // refresh moves its IP — otherwise they'd be stuck on a static
            // operator-shared value that goes stale every refresh.
            if (chal.Type == ChallengeType.KingOfTheHill
                && hillByChallenge.TryGetValue(chal.Id, out var hill))
            {
                row.Hill = new AdHillTarget
                {
                    Ip = hill.Ip,
                    Port = hill.Port,
                    LastCheckStatus = hillStatusByChallenge.GetValueOrDefault(chal.Id)?.ToString(),
                    LastRefreshRound = hill.LastRefreshRound
                };
            }

            result.Challenges.Add(row);
        }

        return Ok(result);
    }

    /// <summary>
    /// Returns the freeze cutoff instant for the caller, or null for a live
    /// view. Non-null only when the game has a <see cref="Game.FreezeTimeUtc"/>,
    /// now is within [freeze, end), and the caller is NOT a monitor/admin.
    /// A&amp;D scoreboard + timeline both build against this cutoff so the public
    /// view freezes exactly like the jeopardy board.
    /// </summary>
    private async Task<DateTimeOffset?> ResolveFreezeCutoffAsync(Game game, CancellationToken token)
    {
        if (game.FreezeTimeUtc is not { } freeze) return null;
        var now = DateTimeOffset.UtcNow;
        if (now < freeze || now >= game.EndTimeUtc) return null;
        // Monitors/admins always see the live board.
        return await ContextHelper.HasMonitor(HttpContext) ? null : freeze;
    }

    /// <summary>
    /// Get the A&amp;D scoreboard for this game — independent of the jeopardy
    /// scoreboard. Public; respects the game's hidden flag and ICPC freeze.
    /// </summary>
    [HttpGet("Scoreboard")]
    [ProducesResponseType(typeof(AdScoreboardModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> Scoreboard(int id, CancellationToken token)
    {
        var game = await db.Games.FirstOrDefaultAsync(g => g.Id == id, token);
        if (game is null || game.Hidden) return NotFound();

        var hasAd = await db.GameChallenges.AnyAsync(
            c => c.GameId == id && (c.Type == ChallengeType.AttackDefense
                                 || c.Type == ChallengeType.KingOfTheHill), token);
        if (!hasAd) return NotFound();

        // ICPC-style freeze: during [FreezeTimeUtc, EndTimeUtc) non-monitor
        // viewers see the board as of the freeze instant. Attacks/checks after
        // the cutoff still persist + score; only this public view is frozen.
        var cutoff = await ResolveFreezeCutoffAsync(game, token);
        Response.Headers.Append("Vary", "Cookie");

        // Served from the distributed cache (regenerated in the background on
        // round-advance / submit / checker tick) so the per-service aggregations
        // run once per round, not once per viewer. Cache miss → build inline.
        var board = await adScoreboard.TryGetScoreboardAsync(id, cutoff != null, token)
                    ?? await adScoreboard.GetScoreboardAsync(id, cutoff, token);
        return Ok(board);
    }

    /// <summary>
    /// KotH-only scoreboard for this game — one column per enabled King of the
    /// Hill challenge, one row per team, ranked by total hold points. Strips
    /// A&amp;D services so the dedicated KotH page isn't padded with empty
    /// attack/defense/SLA columns. Public; respects the game's hidden flag and
    /// the ICPC freeze the same way as <c>Scoreboard</c>. Returns an empty
    /// board (not 404) if the game has KotH-engine challenges but none are
    /// enabled — the UI can render "no hills configured" cleanly.
    /// </summary>
    [HttpGet("Koth/Scoreboard")]
    [ProducesResponseType(typeof(KothScoreboardModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> KothScoreboard(int id, CancellationToken token)
    {
        var game = await db.Games.FirstOrDefaultAsync(g => g.Id == id, token);
        if (game is null || game.Hidden) return NotFound();

        var hasKoth = await db.GameChallenges.AnyAsync(
            c => c.GameId == id && c.Type == ChallengeType.KingOfTheHill, token);
        if (!hasKoth) return NotFound();

        var cutoff = await ResolveFreezeCutoffAsync(game, token);
        Response.Headers.Append("Vary", "Cookie");

        var board = await adScoreboard.TryGetKothScoreboardAsync(id, cutoff != null, token)
                    ?? await adScoreboard.GetKothScoreboardAsync(id, cutoff, token);
        return Ok(board);
    }

    /// <summary>
    /// KotH-only score timeline — per-round per-team cumulative hold credit
    /// (Σ HoldCredit − Penalty across all hills). Drives the chart on the
    /// dedicated KotH scoreboard tab; same shape as the A&amp;D timeline so the
    /// front-end can reuse the AdScoreTimeLine echarts component.
    /// </summary>
    [HttpGet("Koth/Timeline")]
    [ProducesResponseType(typeof(AdScoreTimelineModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> KothTimeline(int id, CancellationToken token)
    {
        var game = await db.Games.FirstOrDefaultAsync(g => g.Id == id, token);
        if (game is null || game.Hidden) return NotFound();

        var hasKoth = await db.GameChallenges.AnyAsync(
            c => c.GameId == id && c.Type == ChallengeType.KingOfTheHill, token);
        if (!hasKoth) return NotFound();

        var cutoff = await ResolveFreezeCutoffAsync(game, token);
        Response.Headers.Append("Vary", "Cookie");

        var result = await adScoreboard.TryGetKothTimelineAsync(id, cutoff != null, token)
                     ?? await adScoreboard.GetKothTimelineAsync(id, cutoff, token);
        return Ok(result);
    }

    /// <summary>
    /// Per-round, per-team cumulative score timeline for the A&amp;D scoreboard
    /// chart. Mirrors what GameRepository builds for the jeopardy ScoreTimeLine
    /// component. Public; respects the game's hidden flag.
    /// </summary>
    [HttpGet("Timeline")]
    [ProducesResponseType(typeof(AdScoreTimelineModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> Timeline(int id, CancellationToken token)
    {
        var game = await db.Games.FirstOrDefaultAsync(g => g.Id == id, token);
        if (game is null || game.Hidden) return NotFound();

        var hasAd = await db.GameChallenges.AnyAsync(
            c => c.GameId == id && (c.Type == ChallengeType.AttackDefense
                                 || c.Type == ChallengeType.KingOfTheHill), token);
        if (!hasAd) return NotFound();

        // ICPC freeze: cap the timeline at the freeze instant for non-monitors.
        var cutoff = await ResolveFreezeCutoffAsync(game, token);
        Response.Headers.Append("Vary", "Cookie");

        // Cached + downsampled (≤150 points/team) — built once per round in the
        // background, not per request. Cache miss → build inline.
        var result = await adScoreboard.TryGetTimelineAsync(id, cutoff != null, token)
                     ?? await adScoreboard.GetTimelineAsync(id, cutoff, token);
        return Ok(result);
    }

    /// <summary>
    /// Download a per-user WireGuard config (.conf) for accessing the A&amp;D
    /// network. Generates a fresh X25519 keypair + assigns a /32 from the
    /// configured client CIDR on first call; subsequent calls return the same
    /// peer's .conf so the matching server-side entry rendered by
    /// <see cref="AdWireGuardSyncService"/> stays valid.
    ///
    /// <para>Server pubkey comes from the shared <c>/wg-config/server.pub</c>
    /// file written by the wireguard sidecar on first boot. Server endpoint
    /// and other knobs read from <c>IConfiguration</c> (env vars
    /// <c>Ad__Vpn__ServerEndpoint</c>, <c>Ad__Vpn__ClientCidr</c>,
    /// <c>Ad__Vpn__Dns</c>, <c>Ad__Vpn__AllowedIps</c>) and fall back to
    /// in-host-test defaults when unset.</para>
    /// </summary>
    [RequireUser]
    [HttpGet("Vpn/Config")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DownloadVpnConfig(
        int id,
        [FromServices] IConfiguration config,
        [FromServices] AdVpnTopology topology,
        CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var participation = await db.Participations
            .FirstOrDefaultAsync(p => p.GameId == id
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);
        if (participation is null) return Forbid();

        var hasAd = await db.GameChallenges.AnyAsync(
            c => c.GameId == id && (c.Type == ChallengeType.AttackDefense
                                 || c.Type == ChallengeType.KingOfTheHill), token);
        if (!hasAd) return NotFound(new RequestResponse("This game has no A&D or KotH challenges"));

        var configDir = config["Ad:Vpn:ConfigDir"] ?? "/wg-config";
        var serverPubKeyPath = Path.Combine(configDir, "server.pub");

        // Server pubkey comes from the sidecar's shared volume. Env var
        // override exists for tests / out-of-cluster deployments.
        var serverPublicKey =
            config["Ad:Vpn:ServerPublicKey"]
            ?? (System.IO.File.Exists(serverPubKeyPath)
                ? (await System.IO.File.ReadAllTextAsync(serverPubKeyPath, token)).Trim()
                : null);

        if (string.IsNullOrEmpty(serverPublicKey))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new RequestResponse(
                "WireGuard server keypair not yet provisioned by the sidecar. Make sure the wireguard service is running and the shared volume is mounted at " + configDir));

        var serverEndpoint = config["Ad:Vpn:ServerEndpoint"] ?? "127.0.0.1:51820";
        var clientCidr = config["Ad:Vpn:ClientCidr"] ?? "10.13.37.0/24";
        var dns = config["Ad:Vpn:Dns"] ?? "1.1.1.1";

        // AllowedIps order of precedence:
        //   1. explicit Ad__Vpn__AllowedIps env override (operator knows best)
        //   2. live-discovered challenge subnets via AdVpnTopology (the path
        //      that "just works" without manual compose / env config)
        //   3. fallback to just the VPN subnet (degraded mode — clients can
        //      only reach each other, not challenges; the operator should
        //      notice + investigate)
        string allowedIps;
        if (config["Ad:Vpn:AllowedIps"] is { Length: > 0 } configured)
        {
            allowedIps = configured;
        }
        else
        {
            var discovered = await topology.GetChallengeSubnetsAsync(configDir, token);
            allowedIps = discovered.Count > 0
                ? $"{clientCidr}, {string.Join(", ", discovered)}"
                : clientCidr;
        }

        var peer = await db.AdVpnPeers
            .FirstOrDefaultAsync(p => p.UserId == user.Id && p.ParticipationId == participation.Id, token);

        string privKeyBase64;
        var xorKey = configService.GetXorKey();

        if (peer is null)
        {
            var (pub, priv) = AdVpnKeys.GenerateX25519KeyPair();

            // Allocate an IP and persist, retrying on the unique (GameId,
            // AssignedIp) constraint: if a concurrent provision grabbed the
            // same IP between our scan and save, re-scan and pick the next one
            // rather than 500-ing.
            const int maxAttempts = 5;
            for (var attempt = 1; ; attempt++)
            {
                // Global across ALL games: every peer renders onto ONE shared
                // wg0 interface (single ClientCidr), so two games assigning the
                // same IP collide in AllowedIPs and break routing for one of them.
                // Uniqueness must be interface-wide, not per-game.
                var usedIps = await db.AdVpnPeers
                    .Select(p => p.AssignedIp)
                    .ToListAsync(token);

                string assignedIp;
                try { assignedIp = AdVpnKeys.AssignNextIp(clientCidr, usedIps); }
                catch (InvalidOperationException ex)
                {
                    return BadRequest(new RequestResponse(ex.Message));
                }

                peer = new AdVpnPeer
                {
                    UserId = user.Id,
                    ParticipationId = participation.Id,
                    GameId = participation.GameId,
                    PublicKey = Convert.ToBase64String(pub),
                    PrivateKey = AdVpnKeys.WrapPrivateKey(priv, xorKey),
                    AssignedIp = assignedIp,
                };
                db.AdVpnPeers.Add(peer);
                try
                {
                    await db.SaveChangesAsync(token);
                    logger.SystemLog(
                        $"A&D VPN peer provisioned: user={user.Id} participation={participation.Id} ip={assignedIp}",
                        TaskStatus.Success, LogLevel.Information);
                    break;
                }
                catch (DbUpdateException)
                {
                    db.Entry(peer).State = EntityState.Detached;
                    // Don't let a unique-violation escape as a 500. This covers both an
                    // AssignedIp collision (retry picks the next free IP) and a concurrent
                    // (UserId, ParticipationId) double-submit (the peer already exists). On
                    // the final attempt return a clean 409 — a client retry then hits the
                    // existing-peer fast path above and gets its .conf.
                    if (attempt >= maxAttempts)
                        return StatusCode(StatusCodes.Status409Conflict,
                            new RequestResponse(
                                "VPN config is being provisioned concurrently; please retry.",
                                StatusCodes.Status409Conflict));
                }
            }

            privKeyBase64 = Convert.ToBase64String(priv);
        }
        else
        {
            privKeyBase64 = AdVpnKeys.UnwrapPrivateKey(peer.PrivateKey, xorKey);
        }

        var safeUserName = string.Concat((user.UserName ?? "player")
            .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'));
        if (string.IsNullOrEmpty(safeUserName)) safeUserName = "player";

        var conf =
$"""
# WireGuard config for {user.UserName} — A&D game {id}
# Generated {DateTimeOffset.UtcNow:u}
# Pubkey: {peer.PublicKey}
# Assigned IP: {peer.AssignedIp}

[Interface]
PrivateKey = {privKeyBase64}
Address = {peer.AssignedIp}/32
DNS = {dns}

[Peer]
PublicKey = {serverPublicKey}
Endpoint = {serverEndpoint}
AllowedIPs = {allowedIps}
PersistentKeepalive = 25
""";

        var bytes = System.Text.Encoding.UTF8.GetBytes(conf);
        return File(bytes, "text/plain", $"ad-game-{id}-{safeUserName}.conf");
    }

    /// <summary>
    /// Download the post-game container snapshot tarball for one of the
    /// caller's team services. Available only after game end + if the
    /// challenge has AdAllowSnapshotDownload=true + a snapshot was actually
    /// taken (Docker provider only for v1).
    /// </summary>
    [RequireUser]
    [HttpGet("Services/{adTeamServiceId:int}/Snapshot")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DownloadSnapshot(int id, int adTeamServiceId, CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var ts = await db.AdTeamServices
            .Include(t => t.Participation)
            .FirstOrDefaultAsync(t => t.Id == adTeamServiceId, token);

        if (ts is null || ts.Participation.GameId != id) return NotFound();

        var isMember = await db.Participations
            .AnyAsync(p => p.Id == ts.ParticipationId
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);
        if (!isMember) return Forbid();

        // Post-game only AND the download policy must still be on. A snapshot key may
        // linger from a prior game-end while the game is running again (extended/restarted),
        // and an operator may have revoked download AFTER capture — honor the live policy,
        // not the state at capture time, or a team could keep pulling their patched image.
        var game = await db.Games.Where(g => g.Id == id)
            .Select(g => new { g.EndTimeUtc, g.AdAllowSnapshotDownload })
            .FirstOrDefaultAsync(token);
        if (game is null) return NotFound();
        if (!game.AdAllowSnapshotDownload)
            return NotFound(new RequestResponse("Snapshot download is disabled for this game"));
        if (DateTimeOffset.UtcNow < game.EndTimeUtc)
            return NotFound(new RequestResponse("Snapshot is only available after the game ends"));

        if (string.IsNullOrEmpty(ts.SnapshotBlobKey))
            return NotFound(new RequestResponse("Snapshot not available (game still running, snapshot disabled, or snapshot failed)"));

        if (!await blobStorage.ExistsAsync(ts.SnapshotBlobKey, token))
        {
            logger.SystemLog($"A&D snapshot blob missing: key={ts.SnapshotBlobKey}",
                TaskStatus.Failed, LogLevel.Warning);
            return NotFound(new RequestResponse("Snapshot blob is missing — may have been retained-out"));
        }

        var stream = await blobStorage.OpenReadAsync(ts.SnapshotBlobKey, token);
        var filename = $"ad-snapshot-team{ts.ParticipationId}-challenge{ts.ChallengeId}.tar.gz";
        return File(stream, "application/gzip", filename);
    }

    /// <summary>
    /// Upload an OpenSSH public key — the recommended path. The private
    /// half never crosses the wire. Overwrites the caller's existing key
    /// if present (rotation = re-upload). One slot per (User, Game) — the
    /// (UserId, ParticipationId) unique index enforces this at the DB.
    /// </summary>
    [RequireUser]
    [HttpPost("Ssh/Key")]
    [ProducesResponseType(typeof(AdSshKeyInfoModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> UploadSshKey(
        int id,
        [FromBody] AdSshKeyUploadModel model,
        [FromServices] IConfiguration config,
        CancellationToken token)
    {
        if (!ModelState.IsValid)
            return BadRequest(new RequestResponse(localizer[nameof(Resources.Program.Model_ValidationFailed)]));

        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var participation = await db.Participations
            .FirstOrDefaultAsync(p => p.GameId == id
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);
        if (participation is null) return Forbid();

        AdSshKeyUtils.ParsedKey parsed;
        try { parsed = AdSshKeyUtils.Parse(model.PublicKey); }
        catch (FormatException e) { return BadRequest(new RequestResponse(e.Message)); }

        // Fingerprint must be unique within the game. The jump host resolves a login by
        // (fingerprint, challengeId) scoped to the game and FAILS CLOSED on ambiguity, so
        // letting a second participation register an existing key would let one team lock
        // another out of SSH — a public key isn't secret (e.g. github.com/<user>.keys).
        // Reject the cross-team collision (a teammate re-using a key, or the caller rotating
        // their own slot, is fine — those share this participation id). Serialize concurrent
        // registrations of the SAME fingerprint in the SAME game under a transaction-scoped
        // advisory lock so the check+insert is race-free (two simultaneous uploads of an
        // identical key by different teams would otherwise both pass the check and insert).
        // Mirrors the A&D /Submit advisory lock.
        var lockKey = $"{id}:{parsed.Fingerprint}";
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtext('gzctf_ad_sshkey'), hashtext({lockKey}))", token);

        var gameParticipationIds = db.Participations.Where(p => p.GameId == id).Select(p => p.Id);
        var collision = await db.AdTeamSshKeys.AnyAsync(k =>
            k.Fingerprint == parsed.Fingerprint
            && k.RevokedAt == null
            && k.ParticipationId != participation.Id
            && gameParticipationIds.Contains(k.ParticipationId), token);
        if (collision)
            return Conflict(new RequestResponse(
                "This SSH key is already registered by another team in this game — use a different key."));

        var now = DateTimeOffset.UtcNow;
        var existing = await db.AdTeamSshKeys
            .FirstOrDefaultAsync(k => k.UserId == user.Id && k.ParticipationId == participation.Id, token);

        if (existing is null)
        {
            existing = new AdTeamSshKey
            {
                UserId = user.Id,
                ParticipationId = participation.Id,
                PublicKey = model.PublicKey.Trim(),
                PrivateKey = null,
                Algorithm = parsed.Algorithm,
                Fingerprint = parsed.Fingerprint,
                PlatformGenerated = false,
                CreatedAt = now
            };
            await db.AdTeamSshKeys.AddAsync(existing, token);
        }
        else
        {
            existing.PublicKey = model.PublicKey.Trim();
            existing.PrivateKey = null;
            existing.Algorithm = parsed.Algorithm;
            existing.Fingerprint = parsed.Fingerprint;
            existing.PlatformGenerated = false;
            existing.RevokedAt = null;
            existing.CreatedAt = now;
            existing.LastUsedAt = null;
        }

        await db.SaveChangesAsync(token);
        await tx.CommitAsync(token);

        logger.SystemLog(
            $"A&D SSH key uploaded: user={user.Id} game={id} fp={parsed.Fingerprint}",
            TaskStatus.Success, LogLevel.Information);

        return Ok(BuildSshInfo(existing, config));
    }

    /// <summary>
    /// Server-generate an ed25519 keypair. The private key is returned
    /// once in this response; the ciphertext is also stored at-rest so
    /// the platform can re-emit it if the user loses the file before
    /// the game ends (operator decision — disable later if undesired).
    /// </summary>
    [RequireUser]
    [HttpPost("Ssh/Key/Generate")]
    [ProducesResponseType(typeof(AdSshKeyGeneratedModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> GenerateSshKey(
        int id,
        [FromServices] IConfiguration config,
        CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var participation = await db.Participations
            .FirstOrDefaultAsync(p => p.GameId == id
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);
        if (participation is null) return Forbid();

        var comment = $"gzctf-ad-{user.UserName}-game{id}";
        var kp = AdSshKeyUtils.GenerateEd25519(comment);
        var xorKey = configService.GetXorKey();
        var wrapped = AdSshKeyUtils.WrapPrivateKey(kp.PrivateKeyOpenSsh, xorKey);
        var now = DateTimeOffset.UtcNow;

        var existing = await db.AdTeamSshKeys
            .FirstOrDefaultAsync(k => k.UserId == user.Id && k.ParticipationId == participation.Id, token);

        if (existing is null)
        {
            existing = new AdTeamSshKey
            {
                UserId = user.Id,
                ParticipationId = participation.Id,
                PublicKey = kp.PublicKeyOpenSsh,
                PrivateKey = wrapped,
                Algorithm = "ssh-ed25519",
                Fingerprint = kp.Fingerprint,
                PlatformGenerated = true,
                CreatedAt = now
            };
            await db.AdTeamSshKeys.AddAsync(existing, token);
        }
        else
        {
            existing.PublicKey = kp.PublicKeyOpenSsh;
            existing.PrivateKey = wrapped;
            existing.Algorithm = "ssh-ed25519";
            existing.Fingerprint = kp.Fingerprint;
            existing.PlatformGenerated = true;
            existing.RevokedAt = null;
            existing.CreatedAt = now;
            existing.LastUsedAt = null;
        }

        await db.SaveChangesAsync(token);

        logger.SystemLog(
            $"A&D SSH keypair generated: user={user.Id} game={id} fp={kp.Fingerprint}",
            TaskStatus.Success, LogLevel.Information);

        return Ok(new AdSshKeyGeneratedModel
        {
            Algorithm = "ssh-ed25519",
            PublicKey = kp.PublicKeyOpenSsh,
            PrivateKey = kp.PrivateKeyOpenSsh,
            Fingerprint = kp.Fingerprint,
            CreatedAt = now
        });
    }

    /// <summary>
    /// Metadata for the caller's installed SSH key (no plaintext returned).
    /// <see cref="AdSshKeyInfoModel.Exists"/> false on first call so the
    /// UI can render the upload form.
    /// </summary>
    [RequireUser]
    [HttpGet("Ssh/Key")]
    [ProducesResponseType(typeof(AdSshKeyInfoModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSshKey(
        int id,
        [FromServices] IConfiguration config,
        CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var participation = await db.Participations
            .FirstOrDefaultAsync(p => p.GameId == id
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);
        if (participation is null) return Forbid();

        var existing = await db.AdTeamSshKeys
            .FirstOrDefaultAsync(k => k.UserId == user.Id && k.ParticipationId == participation.Id, token);

        if (existing is null || existing.RevokedAt is not null)
            return Ok(new AdSshKeyInfoModel { Exists = false, JumpHost = ResolveJumpHost(config) });

        return Ok(BuildSshInfo(existing, config));
    }

    /// <summary>
    /// Revoke the caller's SSH key. Next jump-host connection from the
    /// matching pubkey is refused at <c>AuthorizedKeysCommand</c> time.
    /// </summary>
    [RequireUser]
    [HttpDelete("Ssh/Key")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RevokeSshKey(int id, CancellationToken token)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var participation = await db.Participations
            .FirstOrDefaultAsync(p => p.GameId == id
                && p.Status == ParticipationStatus.Accepted
                && p.Members.Any(m => m.UserId == user.Id), token);
        if (participation is null) return Forbid();

        var existing = await db.AdTeamSshKeys
            .FirstOrDefaultAsync(k => k.UserId == user.Id && k.ParticipationId == participation.Id, token);
        if (existing is null) return NoContent();

        db.AdTeamSshKeys.Remove(existing);
        await db.SaveChangesAsync(token);

        logger.SystemLog(
            $"A&D SSH key revoked: user={user.Id} game={id}",
            TaskStatus.Success, LogLevel.Information);
        return NoContent();
    }

    private static AdSshKeyInfoModel BuildSshInfo(AdTeamSshKey key, IConfiguration config) => new()
    {
        Exists = true,
        Algorithm = key.Algorithm,
        Fingerprint = key.Fingerprint,
        PlatformGenerated = key.PlatformGenerated,
        CreatedAt = key.CreatedAt,
        LastUsedAt = key.LastUsedAt,
        JumpHost = ResolveJumpHost(config)
    };

    private static string ResolveJumpHost(IConfiguration config)
    {
        var host = config["Ad:Ssh:PublicHost"];
        if (string.IsNullOrWhiteSpace(host))
            host = config["PublicEntry"] ?? "localhost";
        var port = config["Ad:Ssh:PublicPort"];
        return string.IsNullOrWhiteSpace(port) ? $"{host}:2222" : $"{host}:{port}";
    }
}
