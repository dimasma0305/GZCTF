using System.Security.Cryptography;
using GZCTF.Models;
using GZCTF.Models.Data;
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
    ILogger<AdRoundService> logger)
{
    private const int FlagRandomBytes = 24;

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

        // Pick the shortest tick across enabled A&D challenges so the round
        // window honors the most-aggressive checker.
        var tickSeconds = adChallenges.Min(c => c.AdTickSeconds ?? 120);

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
            .ToListAsync(token);

        var flagsPlanted = 0;
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
            flagsPlanted++;
        }

        await db.SaveChangesAsync(token);

        logger.SystemLog(
            $"A&D round advanced: game={gameId} round={nextNumber} flags_planted={flagsPlanted}",
            TaskStatus.Success, LogLevel.Information);

        return new Result(round, flagsPlanted);
    }
}
