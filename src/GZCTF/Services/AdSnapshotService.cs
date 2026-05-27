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
public sealed class AdSnapshotService(
    IServiceScopeFactory scopeFactory,
    ILogger<AdSnapshotService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

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
        var manager = scope.ServiceProvider.GetService<AdContainerManager>();
        if (manager is null) return;

        var now = DateTimeOffset.UtcNow;
        var activeGames = await db.Games
            .Where(g => g.StartTimeUtc <= now && now <= g.EndTimeUtc)
            .Where(g => g.Challenges.Any(c => c.Type == ChallengeType.AttackDefense && c.IsEnabled))
            .Select(g => g.Id)
            .ToListAsync(token);

        foreach (var gameId in activeGames)
            await CaptureGameAsync(db, scope.ServiceProvider, manager, gameId, token);
    }

    private static async Task CaptureGameAsync(
        AppDbContext db, IServiceProvider sp, AdContainerManager manager, int gameId, CancellationToken token)
    {
        var latest = await db.AdRounds
            .Where(r => r.GameId == gameId)
            .OrderByDescending(r => r.Number)
            .FirstOrDefaultAsync(token);
        if (latest is null) return; // warmup — no round yet

        var services = await db.AdTeamServices
            .Where(ts => ts.Participation.GameId == gameId
                && ts.ContainerId != null
                && ts.Challenge.Type == ChallengeType.AttackDefense
                && ts.Challenge.IsEnabled)
            .Include(ts => ts.Container)
            .Include(ts => ts.Challenge)
            .ToListAsync(token);

        var dirty = false;
        foreach (var ts in services)
        {
            var changes = await manager.ComputeLiveChangesAsync(sp, ts, token);
            if (changes is null) continue; // no live container / provider error

            // Canonical manifest so dedupe compares stably regardless of scan order.
            var manifest = JsonSerializer.Serialize(
                changes.OrderBy(c => c.Path, StringComparer.Ordinal).ThenBy(c => c.Kind)
                    .Select(c => new { p = c.Path, k = c.Kind }));

            var last = await db.AdServiceSnapshots
                .Where(s => s.AdTeamServiceId == ts.Id)
                .OrderByDescending(s => s.Id)
                .Select(s => s.ManifestJson)
                .FirstOrDefaultAsync(token);
            if (last == manifest) continue; // unchanged since last capture

            await db.AdServiceSnapshots.AddAsync(new AdServiceSnapshot
            {
                AdTeamServiceId = ts.Id,
                AdRoundId = latest.Id,
                CapturedAt = DateTimeOffset.UtcNow,
                ManifestJson = manifest
            }, token);
            dirty = true;
        }

        if (dirty)
            await db.SaveChangesAsync(token);
    }
}
