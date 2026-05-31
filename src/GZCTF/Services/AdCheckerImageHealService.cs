using GZCTF.Models;
using GZCTF.Services.Container.Build;
using GZCTF.Utils;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Services;

/// <summary>
/// Safety net for the local-only autobuilt A&amp;D/KotH checker images. These have
/// no long-running container holding them, so an ad-hoc <c>docker image prune -a</c>
/// (one that doesn't honour the <c>org.gzctf.keep</c> label the daily cron uses)
/// can delete one mid-game — after which every check on that challenge reports
/// InternalError on the failed image pull, freezing SLA accrual for all teams.
///
/// <para>Every <see cref="Interval"/> this scans enabled A&amp;D/KotH challenges in
/// currently-running games and, for any whose <c>gzctf-auto/…</c> checker image has
/// gone missing, rebuilds it from the persisted build context (see
/// <see cref="DockerChallengeImageBuilder"/>) — no re-import needed. It is a
/// cheap no-op when every image is present (one image-inspect per distinct tag).</para>
///
/// <para>No-ops under Kubernetes: the K8s builder pulls from a registry the cluster
/// can reach, so there is no local-only image to lose, and
/// <c>TryRestoreImageAsync</c> returns false there.</para>
/// </summary>
public sealed class AdCheckerImageHealService(
    IServiceScopeFactory scopeFactory,
    IChallengeImageBuilder imageBuilder,
    ILogger<AdCheckerImageHealService> logger) : BackgroundService
{
    // Snappier than the daily prune is rare, but the check tick is 10s — 30s
    // bounds the InternalError window to ~3 ticks after a stray prune.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.SystemLog(
            $"AdCheckerImageHealService started; reconciling autobuilt checker images every {Interval.TotalSeconds:0}s",
            TaskStatus.Pending, LogLevel.Information);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogErrorMessage(e, "AdCheckerImageHealService tick failed; will retry");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTimeOffset.UtcNow;
        var tags = await db.GameChallenges
            .Where(c => (c.Type == ChallengeType.AttackDefense || c.Type == ChallengeType.KingOfTheHill)
                && c.IsEnabled
                && c.AdCheckerImage != null
                && c.AdCheckerImage.StartsWith("gzctf-auto/")
                && c.Game.StartTimeUtc <= now && now <= c.Game.EndTimeUtc)
            .Select(c => c.AdCheckerImage!)
            .Distinct()
            .ToListAsync(token);

        foreach (var tag in tags)
        {
            token.ThrowIfCancellationRequested();
            // No-op when the image is already present; rebuilds it from the
            // persisted context when it has been pruned out from under the game.
            await imageBuilder.TryRestoreImageAsync(tag, token);
        }
    }
}
