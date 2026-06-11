using System.Threading.Channels;
using GZCTF.Repositories.Interface;
using GZCTF.Services.Cache;
using GZCTF.Services.Cache.Handlers;
using GZCTF.Services.Traffic;
using GZCTF.Models.Internal;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

// ReSharper disable UnusedMember.Global

namespace GZCTF.Services.CronJob;

public static class RuntimeCronJobs
{
    /// <summary>Information-level logs older than this are pruned (Warning/Error are kept).</summary>
    const int LogRetentionDays = 30;

    const int LogPruneBatchSize = 5000;

    [CronJob("*/3 * * * *")]
    public static async Task ContainerChecker(AsyncServiceScope scope, ILogger<CronJobService> logger)
    {
        var containerRepo = scope.ServiceProvider.GetRequiredService<IContainerRepository>();
        var trafficRegistry = scope.ServiceProvider.GetRequiredService<TrafficRecorderRegistry>();

        foreach (var container in await containerRepo.GetDyingContainers())
        {
            await trafficRegistry.ArchiveAsync(container.Id);
            await containerRepo.DestroyContainer(container);
            logger.SystemLog(
                StaticLocalizer[nameof(Resources.Program.CronJob_RemoveExpiredContainer),
                    container.LogId],
                TaskStatus.Success, LogLevel.Debug);
        }
    }

    [CronJob("*/10 * * * *")]
    public static async Task BootstrapCache(AsyncServiceScope scope, ILogger<CronJobService> logger)
    {
        var gameRepo = scope.ServiceProvider.GetRequiredService<IGameRepository>();
        var upcoming = await gameRepo.GetUpcomingGames();

        if (upcoming.Length <= 0)
            return;

        var writer = scope.ServiceProvider.GetRequiredService<ChannelWriter<CacheRequest>>();
        var cache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();

        foreach (var game in upcoming)
        {
            var key = CacheKey.ScoreBoard(game);
            if (await cache.GetAsync(key) is not null)
                continue;

            await writer.WriteAsync(ScoreboardCacheHandler.MakeCacheRequest(game));
            logger.SystemLog(StaticLocalizer[nameof(Resources.Program.CronJob_BootstrapRankingCache), key],
                TaskStatus.Success,
                LogLevel.Debug);
        }
    }

    [CronJob("0 * * * *")]
    public static async Task FlushRecentGames(AsyncServiceScope scope, ILogger<CronJobService> logger)
    {
        var helper = scope.ServiceProvider.GetRequiredService<CacheHelper>();

        await helper.FlushRecentGamesCache(CancellationToken.None);
    }

    [CronJob("0 */4 * * *")]
    public static async Task RemoveUnactivatedUsers(AsyncServiceScope scope, ILogger<CronJobService> logger)
    {
        // This reaper deletes accounts that registered but never CONFIRMED THEIR EMAIL — its
        // predicate is `!EmailConfirmed`. That premise only holds when email confirmation is the
        // activation gate. In admin-approval mode (EmailConfirmationRequired=false) a !EmailConfirmed
        // account means "pending admin approval" (password AND OAuth paths create it that way), not
        // an abandoned signup — so running the reaper there silently deletes legitimate registrants
        // before an admin can approve them. Skip entirely unless email confirmation is required.
        var accountPolicy = scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<AccountPolicy>>().Value;
        if (!accountPolicy.EmailConfirmationRequired)
            return;

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserInfo>>();
        var timeThreshold = DateTimeOffset.UtcNow.AddHours(-48);

        var usersToDelete = userManager.Users
            .Where(u => !u.EmailConfirmed && u.RegisterTimeUtc < timeThreshold)
            .ToList();

        if (usersToDelete.Count == 0)
            return;

        var deleted = 0;
        foreach (var user in usersToDelete)
        {
            // Don't let one undeletable user (e.g. a RESTRICT FK) abort the whole run.
            try
            {
                var result = await userManager.DeleteAsync(user);
                if (result.Succeeded)
                    deleted++;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to remove unactivated user {User}", user.Id);
            }
        }

        logger.SystemLog(StaticLocalizer[nameof(Resources.Program.CronJob_RemoveUnactivatedUsers), deleted],
            TaskStatus.Success, LogLevel.Information);
    }

    [CronJob("30 4 * * *")]
    public static async Task PruneOldLogs(AsyncServiceScope scope, ILogger<CronJobService> logger)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-LogRetentionDays);

        // Information-level logs are the bulk of the otherwise-unbounded Logs table and carry no
        // forensic value past the retention window. Delete in bounded batches (by ctid) so a prune
        // never holds a long lock during a live game; Warning/Error rows are retained.
        var total = 0;
        int batch;
        do
        {
            batch = await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 DELETE FROM "Logs" WHERE ctid IN (
                     SELECT ctid FROM "Logs"
                     WHERE "Level" = 'Information' AND "TimeUtc" < {cutoff}
                     LIMIT {LogPruneBatchSize})
                 """);
            total += batch;
        } while (batch == LogPruneBatchSize);

        if (total > 0)
            logger.SystemLog($"Pruned {total} Information-level log row(s) older than {LogRetentionDays} days",
                TaskStatus.Success, LogLevel.Information);
    }
}
