using System.Net.WebSockets;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Repositories.Interface;
using GZCTF.Services.Container.Exec;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Controllers;

/// <summary>
/// East-west endpoints called by the A&amp;D jump-host sidecar — never
/// exposed to the public internet. Auth is a single shared secret
/// (<c>Ad:Ssh:InternalSecret</c>) sent in
/// <c>X-Gzctf-Internal-Auth</c>; the secret is generated at deploy time
/// and only the gzctf + jump-host containers ever see it (compose
/// docker network is the only routable path).
///
/// <para>Endpoints:</para>
/// <list type="bullet">
///   <item><c>GET /Challenges</c> — sidecar bootstraps
///         <c>/etc/passwd</c> entries from this list, one user per
///         A&amp;D challenge id, so sshd will accept connections to
///         <c>ssh &lt;challengeId&gt;@host</c>.</item>
///   <item><c>GET /Lookup</c> — pubkey fingerprint + challengeId →
///         (userId, participationId, containerId). Drives the
///         <c>AuthorizedKeysCommand</c>: returns an
///         <c>authorized_keys</c> line shape, or 404 to reject the
///         connection.</item>
///   <item><c>GET /Exec</c> (WebSocket) — accepts the
///         <c>ForceCommand</c>'s websocat connection, opens
///         <c>docker exec sh</c> against the resolved container, pipes
///         bytes both ways until either side closes.</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/Internal/Ad/Ssh")]
public class InternalAdSshController(
    AppDbContext db,
    IConfiguration configuration,
    IContainerRepository containerRepository,
    IContainerExecChannel execChannel,
    ILogger<InternalAdSshController> logger) : ControllerBase
{
    /// <summary>
    /// All active A&amp;D challenge ids across every running game — the
    /// sidecar materializes a passwd entry per id so sshd accepts
    /// <c>ssh &lt;id&gt;@host</c> regardless of which game/participation
    /// the requester ends up resolving to (that's the
    /// <c>AuthorizedKeysCommand</c>'s job).
    /// </summary>
    [HttpGet("Challenges")]
    public async Task<IActionResult> Challenges(CancellationToken token)
    {
        if (!AuthInternal()) return Unauthorized();

        var ids = await db.GameChallenges
            .Where(c => c.Type == ChallengeType.AttackDefense && c.IsEnabled)
            .Select(c => c.Id)
            .ToListAsync(token);

        return Ok(new InternalAdChallengesModel { ChallengeIds = ids });
    }

    /// <summary>
    /// Resolve <c>(fingerprint, challengeId)</c> to a live container.
    /// 404 means "reject the SSH connection" — either the fingerprint
    /// doesn't match any registered key, the user isn't on a team that
    /// has access to that challenge, or no container is running yet.
    /// </summary>
    [HttpGet("Lookup")]
    public async Task<IActionResult> Lookup(
        [FromQuery] string fingerprint,
        [FromQuery] int challenge,
        CancellationToken token)
    {
        if (!AuthInternal()) return Unauthorized();
        if (string.IsNullOrWhiteSpace(fingerprint) || challenge <= 0)
            return BadRequest();

        var challengeRow = await db.GameChallenges
            .Where(c => c.Id == challenge && c.Type == ChallengeType.AttackDefense && c.IsEnabled)
            .Select(c => new { c.Id, c.GameId, c.Title, c.AdSelfHosted })
            .FirstOrDefaultAsync(token);
        if (challengeRow is null) return NotFound();

        // BYOC (self-hosted): the team's real service runs on THEIR machine —
        // GZCTF only hosts the tunnel relay (recorded as AdTeamService.Container).
        // SSH-jump would docker-exec into that relay, which is meaningless (it's
        // not the service, has no /shared/flag), leaks the relay's env
        // (GZCTF_BYOC_SECRET), and "works" even while the real service is Offline.
        // There is nothing on our side to SSH into, so reject for everyone.
        if (challengeRow.AdSelfHosted)
        {
            logger.LogInformation(
                "InternalAdSsh: rejected SSH on self-hosted (BYOC) challenge {Cid} — the team's service is off-platform; nothing to SSH into here",
                challenge);
            return NotFound();
        }

        // Take(2) to distinguish "exactly one" from "ambiguous": if two
        // participations in this game registered the same key fingerprint we
        // cannot safely decide which team the SSH session belongs to, so fail
        // closed (reject) rather than authenticate the wrong team's box.
        var keyRows = await db.AdTeamSshKeys
            .Include(k => k.Participation).ThenInclude(p => p.Members).ThenInclude(m => m.User)
            .Where(k => k.Fingerprint == fingerprint
                && k.RevokedAt == null
                && k.Participation.GameId == challengeRow.GameId
                && k.Participation.Status == ParticipationStatus.Accepted)
            .Take(2)
            .ToListAsync(token);
        if (keyRows.Count != 1) return NotFound(); // 0 = unknown key; >1 = ambiguous → reject
        var keyRow = keyRows[0];

        // Member-kick = instant revocation (same as the API-token path): the user
        // is only valid while still on the roster AND not banned. The Role != Banned
        // clause closes the same hole as the token/VPN gates — an admin ban sets
        // Role but leaves the roster intact, so without it a banned player keeps
        // SSH access to their box until they're also manually kicked.
        if (!keyRow.Participation.Members.Any(m => m.UserId == keyRow.UserId && m.User.Role != Role.Banned))
            return NotFound();

        var service = await db.AdTeamServices
            .Include(s => s.Container)
            .FirstOrDefaultAsync(s => s.ParticipationId == keyRow.ParticipationId
                && s.ChallengeId == challenge, token);
        if (service?.Container?.ContainerId is not { Length: > 0 } cid)
            return NotFound();

        // Optional "offense-gates-defense" lock: when this challenge has
        // AdSshRequiresFlag set, reject SSH until the team has at least one accepted
        // captured flag for THIS challenge. AdAttacks only stores accepted captures,
        // so Any() == "captured ≥1 flag". 404 = reject the connection (same as the
        // other deny paths above), so the jump host simply refuses.
        var sshRequiresFlag = await db.GameChallenges
            .Where(c => c.Id == challenge)
            .Select(c => c.AdSshRequiresFlag)
            .FirstOrDefaultAsync(token);
        if (sshRequiresFlag
            && !await db.AdAttacks.AnyAsync(
                a => a.AttackerParticipationId == keyRow.ParticipationId
                  && a.ChallengeId == challenge, token))
        {
            logger.LogInformation(
                "InternalAdSsh: rejected SSH for participation {Pid} on challenge {Cid} — no captured flag yet (AdSshRequiresFlag)",
                keyRow.ParticipationId, challenge);
            return NotFound();
        }

        // /Lookup runs at SSH key-OFFER time (AuthorizedKeysCommand), BEFORE sshd
        // verifies the client actually holds the private key — and public keys are
        // public — so an unauthenticated probe offering a victim's pubkey reaches
        // here. Throttle the LastUsedAt write to at most once / 5 min so a probe
        // loop can't amplify DB writes or finely forge the timestamp. (Ideally this
        // moves to /Exec, but the sidecar's ForceCommand doesn't thread the key
        // fingerprint through.)
        var nowUtc = DateTimeOffset.UtcNow;
        if (keyRow.LastUsedAt is null || nowUtc - keyRow.LastUsedAt.Value > TimeSpan.FromMinutes(5))
        {
            keyRow.LastUsedAt = nowUtc;
            await db.SaveChangesAsync(token);
        }

        return Ok(new InternalAdSshLookupModel
        {
            UserId = keyRow.UserId,
            ParticipationId = keyRow.ParticipationId,
            ChallengeId = challenge,
            ChallengeTitle = challengeRow.Title,
            GameId = challengeRow.GameId,
            ContainerId = cid,
            ContainerGuid = service.Container.Id,
            PublicKey = keyRow.PublicKey
        });
    }

    /// <summary>
    /// WebSocket relay. The sidecar's <c>ForceCommand</c> opens a
    /// websocat connection here; we hand the bytes to a Docker exec
    /// session and proxy both directions. Auth is the same shared
    /// secret, sent as the <c>auth</c> query param (browsers can't set
    /// arbitrary WS headers from JS, but the sidecar is a server-side
    /// client so it could — keeping it consistent with the curl-based
    /// REST endpoints though, where header vs query is moot).
    /// </summary>
    [HttpGet("Exec")]
    public async Task<IActionResult> Exec(
        [FromQuery] string auth,
        [FromQuery] Guid container,
        [FromQuery] string shell = "sh",
        CancellationToken token = default)
    {
        if (!AuthInternalValue(auth)) return Unauthorized();
        if (!HttpContext.WebSockets.IsWebSocketRequest)
            return BadRequest();

        var c = await containerRepository.GetContainerById(container, token);
        if (c is null) return NotFound();

        using var ws = await HttpContext.WebSockets.AcceptWebSocketAsync();

        IExecSession session;
        try
        {
            session = await execChannel.OpenAsync(c, string.IsNullOrWhiteSpace(shell) ? "sh" : shell, token);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "InternalAdSsh: exec open failed for container {Cid}", c.ContainerId);
            try { await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "exec failed", CancellationToken.None); }
            catch { /* socket already gone */ }
            return new EmptyResult();
        }

        await using (session)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var ctOut = PumpExecToSocketAsync(session, ws, cts.Token);
            var ctIn = PumpSocketToExecAsync(ws, session, cts.Token);
            await Task.WhenAny(ctOut, ctIn);
            cts.Cancel();
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "session ended", CancellationToken.None); }
            catch { /* socket already gone */ }
        }

        return new EmptyResult();
    }

    private static async Task PumpExecToSocketAsync(IExecSession session, WebSocket ws, CancellationToken token)
    {
        var buf = new byte[4096];
        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            int n;
            try { n = await session.ReadAsync(buf, token); }
            catch (OperationCanceledException) { return; }
            catch { return; }
            if (n == 0) return;
            try
            {
                await ws.SendAsync(buf.AsMemory(0, n), WebSocketMessageType.Binary, true, token);
            }
            catch { return; }
        }
    }

    private static async Task PumpSocketToExecAsync(WebSocket ws, IExecSession session, CancellationToken token)
    {
        var buf = new byte[4096];
        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult res;
            try { res = await ws.ReceiveAsync(buf, token); }
            catch (OperationCanceledException) { return; }
            catch { return; }
            if (res.MessageType == WebSocketMessageType.Close) return;
            if (res.Count == 0) continue;
            try { await session.WriteAsync(buf.AsMemory(0, res.Count), token); }
            catch { return; }
        }
    }

    private bool AuthInternal() =>
        AuthInternalValue(Request.Headers["X-Gzctf-Internal-Auth"].ToString());

    private bool AuthInternalValue(string presented)
    {
        var expected = configuration["Ad:Ssh:InternalSecret"];
        if (string.IsNullOrEmpty(expected))
        {
            // Refuse to operate without a configured secret — otherwise
            // the lookup endpoints would be reachable by anything on the
            // docker network with curl. Operator must set this.
            return false;
        }
        // Fail closed on the SHIPPED placeholder. It is published verbatim in
        // docker-compose.yml, the docs, and this repo, so accepting it would leave a
        // fresh/forgotten deploy guarding an internet-reachable cross-team `docker exec`
        // endpoint with a public constant. Operators MUST set a real secret.
        if (expected == "dev-only-rotate-me-before-prod")
        {
            logger.LogError(
                "Ad:Ssh:InternalSecret is the shipped placeholder — internal SSH endpoints are " +
                "DISABLED until you set AD_SSH_INTERNAL_SECRET to a real secret.");
            return false;
        }
        if (string.IsNullOrEmpty(presented)) return false;
        var a = System.Text.Encoding.UTF8.GetBytes(presented);
        var b = System.Text.Encoding.UTF8.GetBytes(expected);
        if (a.Length != b.Length) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }
}

public class InternalAdChallengesModel
{
    public List<int> ChallengeIds { get; set; } = [];
}

public class InternalAdSshLookupModel
{
    public Guid UserId { get; set; }
    public int ParticipationId { get; set; }
    public int ChallengeId { get; set; }
    public string ChallengeTitle { get; set; } = string.Empty;
    public int GameId { get; set; }
    public string ContainerId { get; set; } = string.Empty;
    public Guid ContainerGuid { get; set; }
    public string PublicKey { get; set; } = string.Empty;
}
