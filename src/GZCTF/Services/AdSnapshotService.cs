using System.Collections.Concurrent;
using System.Text.Json;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Utils;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Services;

/// <summary>
/// Periodically captures each live A&amp;D service's changed-file manifest so an
/// operator can diff a team's service between two points in time (which files
/// they touched between round X and round Y). Deduped: a new
/// <see cref="AdServiceSnapshot"/> row is written only when the change-set
/// differs from that service's previous one, so storage tracks distinct states,
/// not ticks. Reuses <see cref="AdContainerManager.ComputeLiveChangesAsync"/>
/// (Docker InspectChanges / K8s <c>find</c>); silently no-ops when no provider
/// can produce a change list.
/// </summary>
/// <remarks>
/// The change scan is the expensive step (on Kubernetes it execs a whole-rootfs
/// <c>find</c> per container), so it is gated two ways: it runs at most once per
/// round per service (the snapshot semantics are "once per round, deduped" — re-
/// running on every 30s poll for a service whose round hasn't advanced is pure
/// waste), and the per-service scans within a tick run bounded-parallel so a
/// large game can't serialize one tick past the next.
/// </remarks>
public sealed class AdSnapshotService(
    IServiceScopeFactory scopeFactory,
    ILogger<AdSnapshotService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>Max concurrent change scans across all active games — mirrors
    /// AdCheckerService's bound so the snapshotter's exec load is capped the same
    /// way the SLA checker's is.</summary>
    private const int MaxParallel = 8;

    /// <summary>service id → highest round number already scanned. Gates the
    /// scan to once per round per service (see remarks). <see cref="ConcurrentDictionary{TKey,TValue}"/>
    /// because the per-service captures update it in parallel.</summary>
    private readonly ConcurrentDictionary<int, int> _lastRoundByService = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.SystemLog($"AdSnapshotService started; capturing every {PollInterval.TotalSeconds}s",
            TaskStatus.Pending, LogLevel.Information);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickOnceAsync(stoppingToken); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogErrorMessage(e, "AdSnapshotService tick failed; will retry");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickOnceAsync(CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // AdContainerManager is a singleton, so it's safe to resolve once and
        // share across the bounded-parallel per-service captures below.
        var manager = scope.ServiceProvider.GetService<AdContainerManager>();
        if (manager is null) return;

        var now = DateTimeOffset.UtcNow;
        var activeGames = await db.Games
            .Where(g => g.StartTimeUtc <= now && now <= g.EndTimeUtc)
            .Where(g => g.Challenges.Any(c => c.Type == ChallengeType.AttackDefense && c.IsEnabled))
            .Select(g => g.Id)
            .ToListAsync(token);

        foreach (var gameId in activeGames)
            await CaptureGameAsync(manager, gameId, token);
    }

    private async Task CaptureGameAsync(AdContainerManager manager, int gameId, CancellationToken token)
    {
        int roundId, roundNumber;
        List<AdTeamService> services;

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var latest = await db.AdRounds
                .Where(r => r.GameId == gameId)
                .OrderByDescending(r => r.Number)
                .FirstOrDefaultAsync(token);
            if (latest is null) return; // warmup — no round yet
            roundId = latest.Id;
            roundNumber = latest.Number;

            services = await db.AdTeamServices
                .Where(ts => ts.Participation.GameId == gameId
                    && ts.ContainerId != null
                    && ts.Challenge.Type == ChallengeType.AttackDefense
                    && ts.Challenge.IsEnabled)
                .Include(ts => ts.Container)
                .Include(ts => ts.Challenge)
                .AsNoTracking()
                .ToListAsync(token);
        }

        // Gate A: only scan services whose round has advanced since we last
        // scanned them — the find must not re-run on every 30s poll within a round.
        var due = services
            .Where(ts => !_lastRoundByService.TryGetValue(ts.Id, out var seen) || seen < roundNumber)
            .ToList();
        if (due.Count == 0) return;

        // Gate B: bounded-parallel scans, each isolated in its own DI scope/
        // DbContext (EF contexts aren't thread-safe; the singleton manager is).
        using var gate = new SemaphoreSlim(MaxParallel, MaxParallel);
        var tasks = due.Select(async ts =>
        {
            await gate.WaitAsync(token);
            try { await CaptureServiceAsync(manager, ts, roundId, roundNumber, token); }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    private async Task CaptureServiceAsync(
        AdContainerManager manager, AdTeamService ts, int roundId, int roundNumber, CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        var changes = await manager.ComputeLiveChangesAsync(sp, ts, token);
        if (changes is null) return; // no live container / provider error — retry next round

        // Mark scanned for this round even when the dedupe below writes nothing,
        // so the find doesn't re-run on the next poll within the same round.
        _lastRoundByService[ts.Id] = roundNumber;

        // Canonical manifest so dedupe compares stably regardless of scan order.
        var manifest = JsonSerializer.Serialize(
            changes.OrderBy(c => c.Path, StringComparer.Ordinal).ThenBy(c => c.Kind)
                .Select(c => new { p = c.Path, k = c.Kind }));

        var db = sp.GetRequiredService<AppDbContext>();
        // Latest stored manifest for this service. Ordering by AdRoundId (then Id)
        // rides the (AdTeamServiceId, AdRoundId) index.
        var last = await db.AdServiceSnapshots
            .Where(s => s.AdTeamServiceId == ts.Id)
            .OrderByDescending(s => s.AdRoundId)
            .ThenByDescending(s => s.Id)
            .Select(s => s.ManifestJson)
            .FirstOrDefaultAsync(token);
        if (last == manifest) return; // unchanged since last capture

        db.AdServiceSnapshots.Add(new AdServiceSnapshot
        {
            AdTeamServiceId = ts.Id,
            AdRoundId = roundId,
            CapturedAt = DateTimeOffset.UtcNow,
            ManifestJson = manifest
        });
        await db.SaveChangesAsync(token);
    }
}
