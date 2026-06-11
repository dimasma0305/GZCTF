using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Models.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GZCTF.Services;

/// <summary>
/// Context describing a single proxy WebSocket open. Built by
/// <see cref="GZCTF.Controllers.ProxyController"/> and passed to
/// <see cref="IContainerAccessLogger.LogAccess"/>.
/// </summary>
public sealed record ContainerAccessContext(
    Guid ContainerId,
    int ChallengeId,
    int ContainerOwnerParticipationId,
    int GameId,
    Guid? AccessingUserId,
    string? AccessingUserName,
    int? AccessingParticipationId,
    string RemoteIp,
    string? UserAgent,
    bool IsAdmin,
    DateTimeOffset ConnectedAtUtc);

public interface IContainerAccessLogger
{
    /// <summary>
    /// Persist a <see cref="ContainerAccessEvent"/> for the connect and,
    /// if the access is from a different team and the user is not an
    /// admin/monitor, raise <see cref="SuspicionType.CrossTeamContainerAccess"/>.
    /// </summary>
    Task LogAccess(ContainerAccessContext ctx, CancellationToken token = default);
}

public sealed class ContainerAccessLogger(
    AppDbContext db,
    ISuspicionService suspicion,
    IOptions<CheatDetectionConfig> options,
    ILogger<ContainerAccessLogger> logger) : IContainerAccessLogger
{
    public async Task LogAccess(ContainerAccessContext ctx, CancellationToken token = default)
    {
        var cfg = options.Value;
        if (!cfg.LogContainerAccess)
            return;

        try
        {
            var row = new ContainerAccessEvent
            {
                GameId = ctx.GameId,
                ChallengeId = ctx.ChallengeId,
                ContainerOwnerParticipationId = ctx.ContainerOwnerParticipationId,
                ContainerId = ctx.ContainerId,
                AccessingUserId = ctx.AccessingUserId,
                AccessingUserName = ctx.AccessingUserName,
                AccessingParticipationId = ctx.AccessingParticipationId,
                RemoteIp = ctx.RemoteIp,
                UserAgent = ctx.UserAgent,
                ConnectedAtUtc = ctx.ConnectedAtUtc,
            };

            db.ContainerAccessEvents.Add(row);
            await db.SaveChangesAsync(token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "ContainerAccessLogger persist failed for container {Container} (game {Game}, owner pid {Owner})",
                ctx.ContainerId, ctx.GameId, ctx.ContainerOwnerParticipationId);
            // Persistence failure must not block proxy. The suspicion raise below
            // is independent and may still succeed — but if we don't have a row,
            // the resulting SuspicionEvent's Details still carries the context.
        }

        // Raise CrossTeamContainerAccess only when:
        //   - the connecting user is authenticated and resolvable to a participation in this game,
        //   - that participation is NOT the container-owning participation,
        //   - the user is not an admin/monitor (admins legitimately access any container).
        if (ctx.IsAdmin) return;
        if (ctx.AccessingUserId is null) return;
        if (ctx.AccessingParticipationId is null) return;
        if (ctx.AccessingParticipationId == ctx.ContainerOwnerParticipationId) return;

        // Cross-team correlation is a LIVE-game concern. After a game ends, A&D/KotH (and any)
        // challenges relaunch as standard practice containers reached through this SAME proxy
        // path (commit dca9dada), so a post-game practice cross-team open would otherwise pin
        // the top-weight HARD CrossTeamContainerAccess signal on the just-ended game's cheat
        // report. Gate it like the sibling post-game detectors (FlagChecker CheckCheat,
        // ContainerAccessSubmissionDetector, HoneypotChain). The forensic ContainerAccessEvent
        // row written above is kept regardless. The lookup only runs on the cross-team path,
        // not on every proxy open.
        var endTimeUtc = await db.Games
            .Where(g => g.Id == ctx.GameId)
            .Select(g => (DateTimeOffset?)g.EndTimeUtc)
            .FirstOrDefaultAsync(token);
        if (endTimeUtc is { } end && end <= DateTimeOffset.UtcNow) return;

        try
        {
            // Score the ACCESSOR (the cheater who reached into another team's
            // container), NOT the owner. AddSuspicion raises the score on
            // stub.Id, so stub must be the accessing participation; the owner
            // (victim) is recorded only as related metadata. Scoring the owner
            // would let Team B frame Team A with a single cross-team proxy open.
            var stub = new Participation
            {
                Id = ctx.AccessingParticipationId.Value,
                GameId = ctx.GameId,
            };

            // Details format aligns with the frontend's parseDetailLines
            // (monitor/CheatInfo.tsx): semicolons between fields, colon as
            // key/value separator, no other colons in values so each line
            // parses to one label+value pair.
            var details =
                $"accessingUser:{ctx.AccessingUserName};" +
                $"accessingUserId:{ctx.AccessingUserId};" +
                $"accessingPid:{ctx.AccessingParticipationId};" +
                $"containerId:{ctx.ContainerId};" +
                $"remoteIp:{ctx.RemoteIp}";

            await suspicion.AddSuspicion(
                stub,
                SuspicionType.CrossTeamContainerAccess,
                details,
                relatedParticipationId: ctx.ContainerOwnerParticipationId,
                token: token);

            logger.LogWarning(
                "CrossTeamContainerAccess raised: container={Container} owner-pid={Owner} accessing-pid={Accessing} user={User} ip={Ip}",
                ctx.ContainerId, ctx.ContainerOwnerParticipationId, ctx.AccessingParticipationId,
                ctx.AccessingUserName, ctx.RemoteIp);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "ContainerAccessLogger suspicion-raise failed for container {Container}", ctx.ContainerId);
        }
    }
}
