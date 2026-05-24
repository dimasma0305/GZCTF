using Docker.DotNet;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Services.Container.Provider;
using GZCTF.Utils;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Services;

/// <summary>
/// Drives the A&amp;D checker loop: every <see cref="PollInterval"/>,
/// finds <c>(team, challenge)</c> pairs whose latest round has no
/// <see cref="AdCheckResult"/> row yet, dispatches them to
/// <see cref="AdCheckerExecutor"/> with bounded concurrency, persists
/// the outcomes.
///
/// <para>Separate from <see cref="AdRoundService"/> so round advance
/// stays sub-second — a checker run for 30 teams × M challenges adds
/// tens of seconds we don't want on the critical path. The polling
/// model is naturally idempotent on restart: a crashed mid-tick
/// resumes by finding the same un-checked services on the next pass.</para>
///
/// <para>Only the LATEST round per game is checked. Abandoned rounds
/// (e.g. operator restarted gzctf mid-game) stay un-scored on the SLA
/// term — re-running checks against a stale flag would either always
/// fail or, worse, succeed with the wrong flag if challenges leak past
/// flags through caches. Cleaner to let the current round race forward.</para>
///
/// <para>Cadence: 10s. Faster than container reconcile (15s) so we
/// catch services flipping back to Ok quickly after a player patches;
/// slower than the round scheduler (5s) so we don't hammer the DB on
/// the rounds in between.</para>
///
/// <para>K8s deployments: silently no-op (Docker provider not
/// registered). Same posture as <see cref="AdRoundService"/>'s flag
/// inject path.</para>
/// </summary>
public sealed class AdCheckerService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<AdCheckerService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private int MaxParallel => int.TryParse(configuration["Ad:Checker:MaxParallel"], out var n) && n > 0 ? n : 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.SystemLog(
            $"AdCheckerService started; tick every {PollInterval.TotalSeconds}s, max parallel = {MaxParallel}",
            TaskStatus.Pending, LogLevel.Information);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickOnceAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogErrorMessage(e, "AdCheckerService tick failed; will retry");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickOnceAsync(CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Bail silently on K8s (no Docker provider registered). The
        // checker doesn't support k8s exec-to-completion in v1.
        var dockerProvider = scope.ServiceProvider
            .GetService<IContainerProvider<DockerClient, DockerMetadata>>();
        if (dockerProvider is null)
        {
            logger.LogDebug("AdChecker tick: no Docker provider, skipping");
            return;
        }

        var executor = scope.ServiceProvider.GetRequiredService<AdCheckerExecutor>();
        var now = DateTimeOffset.UtcNow;

        var activeGameIds = await db.Games
            .Where(g => g.StartTimeUtc <= now && now <= g.EndTimeUtc)
            .Where(g => g.Challenges.Any(c => c.Type == ChallengeType.AttackDefense && c.IsEnabled))
            .Select(g => g.Id)
            .ToListAsync(token);

        logger.LogDebug("AdChecker tick: {N} active A&D games", activeGameIds.Count);

        foreach (var gameId in activeGameIds)
            await CheckGameAsync(db, executor, gameId, token);
    }

    private async Task CheckGameAsync(
        AppDbContext db, AdCheckerExecutor executor, int gameId, CancellationToken token)
    {
        // Latest round for this game. If null, we're still in warmup —
        // no flags, no checks.
        var latest = await db.AdRounds
            .Where(r => r.GameId == gameId)
            .OrderByDescending(r => r.Number)
            .FirstOrDefaultAsync(token);
        if (latest is null) return;

        // Pull (service, challenge) pairs that:
        //   - belong to an accepted participation in this game
        //   - have a live container
        //   - whose challenge is an enabled A&D challenge
        //   - have NO AdCheckResult yet for this latest round
        // The NOT EXISTS keeps the scheduler idempotent across restarts.
        var pending = await db.AdTeamServices
            .Where(ts => ts.Participation.GameId == gameId
                && ts.Participation.Status == ParticipationStatus.Accepted
                && ts.ContainerId != null
                && ts.Challenge.Type == ChallengeType.AttackDefense
                && ts.Challenge.IsEnabled
                && !db.AdCheckResults.Any(cr =>
                    cr.AdRoundId == latest.Id && cr.AdTeamServiceId == ts.Id))
            .Include(ts => ts.Container)
            .Include(ts => ts.Challenge)
            .ToListAsync(token);

        logger.LogDebug("AdChecker: game={Gid} round={Round} pending={N}",
            gameId, latest.Number, pending.Count);

        if (pending.Count == 0) return;

        // Pre-fetch the planted flag for each (team, challenge) so the
        // executor doesn't need DB access. Round N's flag is what the
        // checker should pass via GZCTF_FLAG.
        var serviceIds = pending.Select(ts => ts.Id).ToList();
        var flagByService = await db.AdFlags
            .Where(f => f.AdRoundId == latest.Id && serviceIds.Contains(f.AdTeamServiceId))
            .Select(f => new { f.AdTeamServiceId, f.Flag })
            .ToDictionaryAsync(f => f.AdTeamServiceId, f => f.Flag, token);

        using var gate = new SemaphoreSlim(MaxParallel, MaxParallel);
        var tasks = new List<Task>(pending.Count);

        foreach (var ts in pending)
        {
            await gate.WaitAsync(token);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    flagByService.TryGetValue(ts.Id, out var flag);
                    var outcome = await executor.RunAsync(ts, latest, ts.Challenge, flag, token);
                    await PersistOutcomeAsync(scopeFactory, ts.Id, latest.Id, outcome, token);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e,
                        "AdChecker: dispatch failed for service={Sid} round={Round}", ts.Id, latest.Number);
                }
                finally
                {
                    gate.Release();
                }
            }, token));
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Insert one <see cref="AdCheckResult"/> row in its own scoped DB
    /// context so parallel writers don't contend on a shared change
    /// tracker.
    /// </summary>
    private static async Task PersistOutcomeAsync(
        IServiceScopeFactory scopeFactory,
        int adTeamServiceId,
        int adRoundId,
        AdCheckerExecutor.CheckOutcome outcome,
        CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Race-safe: another instance/tick may have already inserted a
        // row for this (service, round) pair. Re-check before insert.
        var exists = await db.AdCheckResults
            .AnyAsync(cr => cr.AdRoundId == adRoundId && cr.AdTeamServiceId == adTeamServiceId, token);
        if (exists) return;

        // Per-tick SLA credit is computed once, here, from this service's
        // PREVIOUS verdict — "Ok right after a down tick" is the recovering
        // (half-credit) case. The scoreboard then just SUMs SlaCredit.
        var prevStatus = await db.AdCheckResults
            .Where(cr => cr.AdTeamServiceId == adTeamServiceId && cr.AdRoundId < adRoundId)
            .OrderByDescending(cr => cr.AdRoundId)
            .Select(cr => (AdCheckStatus?)cr.Status)
            .FirstOrDefaultAsync(token);

        await db.AdCheckResults.AddAsync(new AdCheckResult
        {
            AdTeamServiceId = adTeamServiceId,
            AdRoundId = adRoundId,
            Status = outcome.Status,
            SlaCredit = AdScoring.TickCredit(outcome.Status, prevStatus),
            ErrorMessage = outcome.ErrorMessage,
            SourceIp = outcome.SourceIp,
            CheckedAt = DateTimeOffset.UtcNow
        }, token);

        try
        {
            await db.SaveChangesAsync(token);
        }
        catch (DbUpdateException)
        {
            // Unique-ish conflict from another tick winning the race —
            // safe to ignore. (The schema doesn't have a unique index
            // on (round, service) yet, but the re-check above + the
            // 10s cadence makes duplicates a non-issue in practice.)
        }
    }
}
