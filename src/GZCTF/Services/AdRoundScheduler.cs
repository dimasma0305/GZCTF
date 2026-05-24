using GZCTF.Models;
using GZCTF.Models.Data;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Services;

/// <summary>
/// Auto-advances A&amp;D rounds when the current round's tick window expires.
/// Polls every <see cref="PollInterval"/>; for each active A&amp;D game
/// (start ≤ now ≤ end, has enabled A&amp;D challenges), either:
/// <list type="bullet">
///   <item>Bootstraps round 1 once the game has been live past its
///         <see cref="Game.AdWarmupSeconds"/> warmup window, OR</item>
///   <item>Advances to the next round when the latest round's
///         <see cref="AdRound.EndsAt"/> is in the past</item>
/// </list>
///
/// <para>The actual round insert + flag plant goes through
/// <see cref="AdRoundService"/> — the exact same code path the admin
/// "Force advance" button uses, so manual + auto can't diverge.</para>
///
/// <para>Cadence: 5s. Drift between round-end and next-round-start is bounded
/// by this. For a typical 60-180s tick, ≤ 5s slip is acceptable. Tighten the
/// constant if your game needs sub-second precision.</para>
/// </summary>
public sealed class AdRoundScheduler(
    IServiceScopeFactory scopeFactory,
    ILogger<AdRoundScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.SystemLog(
            $"AdRoundScheduler started; tick every {PollInterval.TotalSeconds}s",
            TaskStatus.Pending, LogLevel.Information);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickOnceAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogErrorMessage(e, "AdRoundScheduler tick failed; will retry");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickOnceAsync(CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var advancer = scope.ServiceProvider.GetRequiredService<AdRoundService>();

        var now = DateTimeOffset.UtcNow;

        var activeGames = await db.Games
            .Where(g => g.StartTimeUtc <= now && now <= g.EndTimeUtc)
            .Where(g => g.Challenges.Any(c => c.Type == ChallengeType.AttackDefense && c.IsEnabled))
            .Select(g => new
            {
                g.Id,
                g.StartTimeUtc,
                Warmup = g.AdWarmupSeconds ?? 1800
            })
            .ToListAsync(token);

        foreach (var g in activeGames)
        {
            var latest = await db.AdRounds
                .Where(r => r.GameId == g.Id)
                .OrderByDescending(r => r.Number)
                .Select(r => new { r.Number, r.EndsAt })
                .FirstOrDefaultAsync(token);

            bool needsAdvance;
            if (latest is null)
            {
                // No rounds yet → bootstrap round 1, but only after warmup.
                var warmupEnd = g.StartTimeUtc.AddSeconds(g.Warmup);
                needsAdvance = now >= warmupEnd;
            }
            else
            {
                // Current round's tick window expired → time for the next.
                needsAdvance = latest.EndsAt <= now;
            }

            if (!needsAdvance) continue;

            try
            {
                var result = await advancer.AdvanceAsync(g.Id, token);
                if (result is null) continue;

                logger.LogDebug(
                    "AdRoundScheduler: auto-advanced game {GameId} to round {Round} ({Flags} flags planted)",
                    g.Id, result.Round.Number, result.FlagsPlanted);
            }
            catch (Exception e)
            {
                logger.LogErrorMessage(e,
                    $"AdRoundScheduler: failed to advance game={g.Id}");
            }
        }
    }
}
