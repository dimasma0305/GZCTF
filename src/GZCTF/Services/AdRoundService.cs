using System.Security.Cryptography;
using Docker.DotNet;
using Docker.DotNet.Models;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Services.Container.Provider;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Services;

/// <summary>
/// Shared round-advance + flag-plant logic for A&amp;D games. Used by both:
/// <list type="bullet">
///   <item>The admin "Advance round" button (operator-driven force advance)</item>
///   <item>The <see cref="AdRoundScheduler"/> background service (auto
///         tick-end advance after the game's warmup window expires)</item>
/// </list>
/// Keeping a single code path means the auto-advancer and the manual button
/// can never diverge on scoring semantics — same flag format, same plant-all-
/// teams behavior, same tick-window math.
/// </summary>
public sealed class AdRoundService(
    AppDbContext db,
    IServiceProvider serviceProvider,
    ILogger<AdRoundService> logger)
{
    private const int FlagRandomBytes = 24;
    private const string FlagFilePath = "/flag";

    public sealed record Result(AdRound Round, int FlagsPlanted);

    /// <summary>
    /// Insert the next AdRound for the game + plant a fresh flag for every
    /// (team, enabled A&amp;D challenge) with a live container. Returns the
    /// new round + the flag-plant count, or null if the game has no enabled
    /// A&amp;D challenges (which the caller treats as "nothing to do").
    /// </summary>
    public async Task<Result?> AdvanceAsync(int gameId, CancellationToken token)
    {
        var adChallenges = await db.GameChallenges
            .Where(c => c.GameId == gameId && c.Type == ChallengeType.AttackDefense && c.IsEnabled)
            .ToListAsync(token);

        if (adChallenges.Count == 0)
            return null;

        var prev = await db.AdRounds
            .Where(r => r.GameId == gameId)
            .OrderByDescending(r => r.Number)
            .FirstOrDefaultAsync(token);

        var nextNumber = (prev?.Number ?? 0) + 1;

        // Tick length is an event-wide knob on the game — a round spans the
        // whole game, so every A&D service shares one tick window.
        var tickSeconds = await db.Games
            .Where(g => g.Id == gameId)
            .Select(g => g.AdTickSeconds)
            .FirstOrDefaultAsync(token) ?? 120;

        var now = DateTimeOffset.UtcNow;
        var round = new AdRound
        {
            GameId = gameId,
            Number = nextNumber,
            StartedAt = now,
            EndsAt = now.AddSeconds(tickSeconds)
        };
        await db.AdRounds.AddAsync(round, token);
        await db.SaveChangesAsync(token);

        var services = await db.AdTeamServices
            .Where(ts => ts.Participation.GameId == gameId)
            .Include(ts => ts.Container)
            .ToListAsync(token);

        // (service, flag) pairs we need to push into containers after the
        // DB save commits. Collected up-front so the exec calls don't race
        // with the row inserts.
        var toInject = new List<(AdTeamService Service, string Flag)>();

        foreach (var ts in services)
        {
            var bytes = new byte[FlagRandomBytes];
            RandomNumberGenerator.Fill(bytes);
            var payload = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '_').Replace('/', '-');
            var flag = $"flag{{{payload}}}";

            await db.AdFlags.AddAsync(new AdFlag
            {
                AdRoundId = round.Id,
                AdTeamServiceId = ts.Id,
                PlantedAtRound = nextNumber,
                Flag = flag,
                PlantedAt = now
            }, token);
            toInject.Add((ts, flag));
        }

        await db.SaveChangesAsync(token);

        // Push each flag into its container via `docker exec`. Best-effort —
        // a failed exec only loses one tick of attackability for that team;
        // the next round will retry. Skipped on K8s deploys (no Docker
        // provider registered).
        var docker = serviceProvider
            .GetService<IContainerProvider<DockerClient, DockerMetadata>>()
            ?.GetProvider();
        int injected = 0;
        if (docker is not null)
        {
            foreach (var (ts, flag) in toInject)
            {
                if (ts.Container?.ContainerId is not { Length: > 0 } cid) continue;
                try
                {
                    await WriteFlagFileAsync(docker, cid, flag, token);
                    injected++;
                }
                catch (Exception e)
                {
                    logger.LogWarning(e,
                        "A&D flag inject failed for team={Tid} challenge={Cid}",
                        ts.ParticipationId, ts.ChallengeId);
                }
            }
        }

        logger.SystemLog(
            $"A&D round advanced: game={gameId} round={nextNumber} flags_planted={toInject.Count} flags_injected={injected}",
            TaskStatus.Success, LogLevel.Information);

        return new Result(round, toInject.Count);
    }

    /// <summary>
    /// Write the round's flag to <see cref="FlagFilePath"/> inside a running
    /// container via the Docker exec API. Flag chars are restricted to URL-
    /// safe base64 + the literal <c>flag{}</c> wrapper (see
    /// <c>AdvanceAsync</c>'s generator), so wrapping in single quotes is
    /// shell-safe — no escape needed.
    /// </summary>
    private static async Task WriteFlagFileAsync(
        DockerClient docker, string containerId, string flag, CancellationToken token)
    {
        var exec = await docker.Exec.CreateContainerExecAsync(containerId,
            new ContainerExecCreateParameters
            {
                AttachStdout = false,
                AttachStderr = false,
                Cmd = new[]
                {
                    "sh", "-c",
                    $"printf '%s' '{flag}' > {FlagFilePath} && chmod 644 {FlagFilePath}"
                }
            }, token);

        await docker.Exec.StartContainerExecAsync(exec.ID,
            new ContainerExecStartParameters(), token);
    }
}
