using System.Threading.Channels;
using GZCTF.Services.Cache.Handlers;
using MemoryPack;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;

namespace GZCTF.Services.Cache;

/// <summary>
/// Cache update request
/// </summary>
public class CacheRequest(
    string key,
    DistributedCacheEntryOptions? options = null,
    params string[] @params)
{
    public DateTimeOffset Time { get; } = DateTimeOffset.Now;
    public string Key { get; } = key;
    public string[] Params { get; } = @params;
    public DistributedCacheEntryOptions? Options { get; } = options;
}

/// <summary>
/// Cache request handler
/// </summary>
public interface ICacheRequestHandler
{
    public string? CacheKey(CacheRequest request);
    public Task<byte[]> Handle(AsyncServiceScope scope, CacheRequest request, CancellationToken token = default);
}

public class CacheMaker(
    ILogger<CacheMaker> logger,
    IDistributedCache cache,
    IMemoryCache memoryCache,
    ChannelReader<CacheRequest> channelReader,
    IServiceScopeFactory serviceScopeFactory,
    CacheHelper cacheHelper) : IHostedService
{
    private readonly Dictionary<string, ICacheRequestHandler> _cacheHandlers = new();
    private CancellationTokenSource TokenSource { get; set; } = new();

    public async Task StartAsync(CancellationToken token)
    {
        TokenSource = new CancellationTokenSource();

        #region Add Handlers

        AddCacheRequestHandler<ScoreboardCacheHandler>(CacheKey.ScoreBoardBase);
        AddCacheRequestHandler<ScoreboardFrozenCacheHandler>(CacheKey.ScoreBoardFrozenBase);
        AddCacheRequestHandler<AdScoreboardCacheHandler>(CacheKey.AdScoreBoardBase);
        AddCacheRequestHandler<AdTimelineCacheHandler>(CacheKey.AdTimelineBase);
        AddCacheRequestHandler<KothScoreboardCacheHandler>(CacheKey.KothScoreboardBase);
        AddCacheRequestHandler<KothTimelineCacheHandler>(CacheKey.KothTimelineBase);
        AddCacheRequestHandler<RecentGamesCacheHandler>(CacheKey.RecentGames);
        AddCacheRequestHandler<GameListCacheHandler>(CacheKey.GameList);

        #endregion

        await Task.Factory.StartNew(() => Maker(TokenSource.Token), token, TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public Task StopAsync(CancellationToken token)
    {
        TokenSource.Cancel();

        logger.SystemLog(StaticLocalizer[nameof(Resources.Program.Cache_Stopped)], TaskStatus.Success,
            LogLevel.Debug);

        return Task.CompletedTask;
    }

    public void AddCacheRequestHandler<T>(string key) where T : ICacheRequestHandler, new() =>
        _cacheHandlers.Add(key, new T());

    private async Task Maker(CancellationToken token = default)
    {
        logger.SystemLog(StaticLocalizer[nameof(Resources.Program.Cache_WorkerStarted)], TaskStatus.Pending,
            LogLevel.Debug);

        try
        {
            await foreach (var item in channelReader.ReadAllAsync(token))
            {
                try
                {
                    await ProcessCacheRequestAsync(item, token);
                }
                catch (OperationCanceledException)
                {
                    throw; // shutdown — exit the worker loop
                }
                catch (Exception e)
                {
                    // A transient fault in ONE request (notably a Redis blip in the lock
                    // acquire/release that sits outside the inner try) must skip only that
                    // request, NOT kill the worker. Before this guard such a throw escaped the
                    // OperationCanceledException-only catch below and terminated the loop
                    // permanently — every scoreboard froze at its last snapshot until a process
                    // restart while scoring kept advancing in Postgres. Log and keep going.
                    logger.SystemLog(
                        StaticLocalizer[nameof(Resources.Program.Cache_UpdateWorkerFailed), item.Key, e.Message],
                        TaskStatus.Failed, LogLevel.Error);
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.SystemLog(StaticLocalizer[nameof(Resources.Program.Cache_WorkerCancelled)], TaskStatus.Exit,
                LogLevel.Debug);
        }
        finally
        {
            logger.SystemLog(StaticLocalizer[nameof(Resources.Program.Cache_WorkerStopped)], TaskStatus.Exit,
                LogLevel.Debug);
        }
    }

    /// <summary>
    /// Rebuild the cache for a single request. Any exception it throws is caught by the
    /// caller loop (so it skips only this request, never kills the worker). Returns early
    /// on an unroutable request, a null key, or a lost lock race.
    /// </summary>
    private async Task ProcessCacheRequestAsync(CacheRequest item, CancellationToken token)
    {
        if (!_cacheHandlers.TryGetValue(item.Key, out var handler))
        {
            logger.SystemLog(
                StaticLocalizer[nameof(Resources.Program.Cache_NoMatchingRequest), item.Key],
                TaskStatus.NotFound,
                LogLevel.Warning);
            return;
        }

        var key = handler.CacheKey(item);

        if (key is null)
        {
            logger.SystemLog(
                StaticLocalizer[nameof(Resources.Program.Cache_InvalidUpdateRequest), item.Key],
                TaskStatus.NotFound,
                LogLevel.Warning);
            return;
        }

        var updateLock = CacheKey.UpdateLock(key);

        // Shares CacheHelper's atomic acquire (a single SET-if-not-exists when
        // Redis is configured) rather than its own separate get-then-set: the two
        // used to race each other too, since both write the SAME lock key — and
        // after CacheHelper's fix moved that key to a raw Redis STRING, a
        // still-HASH-based acquire here would've been a straight-up type mismatch,
        // not just a race.
        if (!await cacheHelper.TryAcquireLockAsync(updateLock, token))
        {
            // Someone else (this instance or another replica) is already
            // rebuilding this key — skip rather than duplicate the work.
            logger.SystemLog(StaticLocalizer[nameof(Resources.Program.Cache_InvalidUpdateRequest), key],
                TaskStatus.Pending,
                LogLevel.Debug);
            return;
        }

        try
        {
            var lastUpdateKey = CacheKey.LastUpdateTime(key);
            var lastUpdateBytes = await cache.GetAsync(lastUpdateKey, token);
            if (lastUpdateBytes is not null && lastUpdateBytes.Length > 0)
            {
                var lastUpdate = MemoryPackSerializer.Deserialize<DateTimeOffset>(lastUpdateBytes);
                // if the cache is updated after the request, skip
                // this will de-bounced the slow cache update request
                if (lastUpdate > item.Time)
                    return;
            }

            lastUpdateBytes = MemoryPackSerializer.Serialize(DateTimeOffset.UtcNow);
            await cache.SetAsync(lastUpdateKey, lastUpdateBytes, new(), token);

            await using var scope = serviceScopeFactory.CreateAsyncScope();

            var bytes = await handler.Handle(scope, item, token);

            if (bytes.Length > 0)
            {
                await cache.SetAsync(key, bytes, item.Options ?? new DistributedCacheEntryOptions(), token);
                logger.SystemLog(
                    StaticLocalizer[
                        nameof(Resources.Program.Cache_Updated),
                        key, item.Time.ToString("HH:mm:ss.fff"), bytes.Length
                    ], TaskStatus.Success, LogLevel.Debug);

                // notify local memory cache
                memoryCache.Remove(key);
            }
            else
            {
                logger.SystemLog(StaticLocalizer[nameof(Resources.Program.Cache_GenerationFailed), key],
                    TaskStatus.Failed,
                    LogLevel.Warning);
            }
        }
        finally
        {
            await cacheHelper.ReleaseLockAsync(updateLock, token);
        }

        token.ThrowIfCancellationRequested();
    }
}
