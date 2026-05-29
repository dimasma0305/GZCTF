using System.Net;
using System.Security.Claims;
using GZCTF.Hubs;
using GZCTF.Hubs.Clients;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Models.Internal;
using GZCTF.Models.Request.Admin;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Services;

public class HoneypotService(
    IServiceScopeFactory scopeFactory,
    ISuspicionService suspicionService,
    IHubContext<AdminHub, IAdminClient> hubContext,
    ILogger<HoneypotService> logger) : IHoneypotService
{
    private static readonly TimeSpan IpAttributionWindow = TimeSpan.FromMinutes(60);
    private const int IpAttributionCandidateCap = 500;

    public Task RecordHit(
        HttpContext context,
        string bait,
        string category,
        string? ruleCode = null,
        CancellationToken token = default)
    {
        var ua = context.Request.Headers.UserAgent.ToString();
        var notice = new HoneypotHitModel
        {
            Time = DateTimeOffset.UtcNow,
            Bait = bait,
            Category = category,
            Method = context.Request.Method,
            IP = context.Connection.RemoteIpAddress,
            UserAgent = string.IsNullOrEmpty(ua) ? null : ua,
            Attributed = false
        };
        return RecordAndBroadcast(notice, ruleCode ?? SuspicionType.HoneypotHit, context.User, probe: null, token);
    }

    public Task RecordTcpHit(
        IPAddress? remoteIp,
        string bait,
        string? probe,
        string? ruleCode = null,
        CancellationToken token = default)
    {
        var notice = new HoneypotHitModel
        {
            Time = DateTimeOffset.UtcNow,
            Bait = bait,
            Category = "protocol",
            Method = "TCP",
            IP = remoteIp,
            UserAgent = null,
            Attributed = false
        };
        return RecordAndBroadcast(notice, ruleCode ?? SuspicionType.HoneypotProtocolHit, principal: null, probe, token);
    }

    private async Task RecordAndBroadcast(
        HoneypotHitModel notice,
        string ruleCode,
        ClaimsPrincipal? principal,
        string? probe,
        CancellationToken token)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserInfo>>();

            var attribution = await ResolveAttribution(dbContext, userManager, principal, notice.IP, notice.Time, token);
            notice.UserName = attribution.UserName;
            notice.TeamName = attribution.TeamName;

            if (attribution.Participation is { } participation)
            {
                var details = BuildDetails(notice, probe);
                await suspicionService.AddSuspicion(participation, ruleCode, details, token: token);
                notice.Attributed = true;
            }

            logger.LogWarning(
                "Honeypot hit: bait={Bait} category={Category} method={Method} ip={Ip} ua={UA} user={User} team={Team} probeLen={ProbeLen} attributed={Attributed}",
                notice.Bait, notice.Category, notice.Method, notice.IP, Truncate(notice.UserAgent, 100),
                notice.UserName, notice.TeamName, probe?.Length ?? 0, notice.Attributed);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "HoneypotService record failed for bait={Bait}", notice.Bait);
        }

        try
        {
            await hubContext.Clients.All.ReceivedHoneypotHit(notice);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "HoneypotService broadcast failed for bait={Bait}", notice.Bait);
        }
    }

    private static string BuildDetails(HoneypotHitModel notice, string? probe)
    {
        var ua = Truncate(notice.UserAgent, 200);
        var probeFragment = string.IsNullOrEmpty(probe) ? string.Empty : $" probe={Truncate(probe, 200)}";
        return $"bait={notice.Bait} category={notice.Category} method={notice.Method} ip={notice.IP} ua={ua}{probeFragment}";
    }

    private static async Task<(Participation? Participation, string? UserName, string? TeamName)> ResolveAttribution(
        AppDbContext db,
        UserManager<UserInfo> userManager,
        ClaimsPrincipal? principal,
        IPAddress? ip,
        DateTimeOffset now,
        CancellationToken token)
    {
        if (principal?.Identity?.IsAuthenticated == true)
        {
            var user = await userManager.GetUserAsync(principal);
            if (user is not null)
            {
                var p = await FindActiveParticipationForUser(db, user.Id, now, token);
                if (p is not null)
                    return (p, user.UserName, p.Team.Name);
            }
        }

        if (ip is null) return (null, null, null);

        // Fall back to recent IP→user matches from the application log.
        var since = now - IpAttributionWindow;
        // Resolve recent users seen on THIS exact IP. Filter by IP IN SQL (not after a
        // .Take cap) — otherwise on a busy platform the most-recent N rows are dominated by
        // other IPs and the target IP's rows get truncated out, silently dropping
        // attribution. Only attribute when the IP maps to EXACTLY ONE distinct user: a
        // shared egress (NAT/VPN/CGNAT) shows several, and a honeypot hit is a HARD signal
        // we must not pin on an innocent team. (Npgsql maps IPAddress -> inet, translates ==.)
        var ipUserNames = await db.Logs
            .AsNoTracking()
            .Where(l => l.TimeUtc >= since && l.UserName != null && l.RemoteIP != null && l.RemoteIP == ip)
            .Select(l => l.UserName!)
            .Distinct()
            .Take(2)
            .ToListAsync(token);

        if (ipUserNames.Count != 1) return (null, null, null);
        var recentUserName = ipUserNames[0];

        var resolved = await userManager.FindByNameAsync(recentUserName);
        if (resolved is null) return (null, recentUserName, null);

        var participation = await FindActiveParticipationForUser(db, resolved.Id, now, token);
        return participation is null
            ? (null, resolved.UserName, null)
            : (participation, resolved.UserName, participation.Team.Name);
    }

    private static Task<Participation?> FindActiveParticipationForUser(
        AppDbContext db,
        Guid userId,
        DateTimeOffset now,
        CancellationToken token) =>
        db.Participations
            .AsNoTracking()
            .Include(p => p.Team)
            .Include(p => p.Game)
            .Where(p => p.Game.StartTimeUtc <= now && now <= p.Game.EndTimeUtc)
            // Only ACCEPTED participations compete — don't pin honeypot suspicion on a team
            // that's merely Pending, or one already Rejected/Suspended (e.g. kicked).
            .Where(p => p.Status == ParticipationStatus.Accepted)
            .Where(p => p.Members.Any(m => m.UserId == userId))
            .OrderByDescending(p => p.Game.StartTimeUtc)
            .FirstOrDefaultAsync(token);

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max]);
}
