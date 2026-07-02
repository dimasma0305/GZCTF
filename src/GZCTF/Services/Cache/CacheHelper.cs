using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using GZCTF.Services.Cache.Handlers;
using MemoryPack;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace GZCTF.Services.Cache;

public class CacheHelper(
    IDistributedCache distributedCache,
    IMemoryCache memoryCache,
    ChannelWriter<CacheRequest> channelWriter,
    IConnectionMultiplexer? redis = null)
{
    /// <summary>
    /// Per-cache-key, in-process single-flight gate. The L1 <see cref="IMemoryCache"/> entry
    /// expires every few seconds, and plain <c>MemoryCache.GetOrCreateAsync</c> does NOT
    /// dedupe concurrent factory calls — so on each expiry a thundering herd of concurrent
    /// viewers would all run the expensive distributed-cache + rebuild path at once (this is
    /// the cold-start / refresh stampede that caused timeouts under load). Collapsing the herd
    /// to a single rebuild per key, with the rest awaiting the gate and then reading the freshly
    /// populated L1 entry, is the dominant fix for a single-instance deployment. Keys are bounded
    /// (a handful per game), so the dictionary never grows unbounded.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SingleFlight = new();

    /// <summary>
    /// Get or create cache, if cache not exists will block.
    /// Use local memory cache first to reduce the pressure on distributed cache.
    /// Use CacheMaker and CacheRequest to replace handling longer time operation
    /// </summary>
    public async Task<TResult> GetOrCreateAsync<TResult, TLogger>(
        ILogger<TLogger> logger,
        string key,
        Func<DistributedCacheEntryOptions, Task<TResult>> func,
        MemoryCacheEntryOptions? memoryCacheOptions = null,
        CancellationToken token = default)
    {
        // Fast path: serve a warm L1 hit with no lock at all.
        if (memoryCache.TryGetValue(key, out TResult? cached) && cached is not null)
            return cached;

        // Slow path: single-flight per key so a refresh/cold-start stampede rebuilds ONCE.
        var gate = SingleFlight.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            // Double-check: a holder we queued behind may have just populated L1.
            if (memoryCache.TryGetValue(key, out cached) && cached is not null)
                return cached;

            var value = await GetOrCreateFromDistributedCacheAsync(logger, key, func, token);
            if (value is not null) // don't negatively-cache a failed/transient build
                memoryCache.Set(key, value, memoryCacheOptions ?? CommonMemoryCacheOptions);
            return value;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Get cache value, return null if not exists.
    /// </summary>
    public async Task<TResult?> GetAsync<TResult>(string key, CancellationToken token = default)
    {
        if (memoryCache.TryGetValue(key, out TResult? value) && value is not null)
            return value;

        var bytes = await distributedCache.GetAsync(key, token);
        if (TryDeserialize(bytes, ref value))
            memoryCache.Set(key, value, CommonMemoryCacheOptions);

        return value;
    }

    public Task<string?> GetStringAsync(string key, CancellationToken token = default)
    {
        if (memoryCache.TryGetValue(key, out string? value) && value is not null)
            return Task.FromResult<string?>(value);

        return distributedCache.GetStringAsync(key, token);
    }

    public async Task SetStringAsync(string key, string value, DistributedCacheEntryOptions options,
        CancellationToken token = default)
    {
        await distributedCache.SetStringAsync(key, value, options, token);
        memoryCache.Set(key, value, CommonMemoryCacheOptions);
    }

    public async Task RemoveAsync(string key, CancellationToken token)
    {
        await distributedCache.RemoveAsync(key, token);
        memoryCache.Remove(key);
    }

    public async Task FlushScoreboardCache(int gameId, CancellationToken token)
    {
        await channelWriter.WriteAsync(ScoreboardCacheHandler.MakeCacheRequest(gameId), token);
        await channelWriter.WriteAsync(ScoreboardFrozenCacheHandler.MakeCacheRequest(gameId), token);
    }

    /// <summary>Regenerate the live A&amp;D scoreboard + timeline for a game (the
    /// frozen variants are static within the freeze window, so they aren't flushed —
    /// they rebuild on first read). Call on round-advance, accepted submit, and after
    /// a checker tick.</summary>
    public async Task FlushAdScoreboardCache(int gameId, CancellationToken token)
    {
        await channelWriter.WriteAsync(AdScoreboardCacheHandler.MakeCacheRequest(gameId), token);
        await channelWriter.WriteAsync(AdTimelineCacheHandler.MakeCacheRequest(gameId), token);
        // KotH-only board moves on exactly the same events (round advance,
        // checker tick) — invalidate it alongside the combined boards so the
        // dedicated KotH page never reads a stale tick.
        await channelWriter.WriteAsync(KothScoreboardCacheHandler.MakeCacheRequest(gameId), token);
        await channelWriter.WriteAsync(KothTimelineCacheHandler.MakeCacheRequest(gameId), token);
    }

    /// <summary>
    /// Flush the LIVE A&amp;D/KotH boards AND drop the four FROZEN variants. Call
    /// whenever a challenge's <c>IsEnabled</c> (or another scoring input the frozen
    /// build filters on) changes mid-game: the live boards self-heal on the next
    /// tick, but the frozen boards (7-day sliding, never regenerated) would otherwise
    /// serve a stale column + wrong ranks for the rest of the freeze. Used by the
    /// AdOps toggle, the challenge-edit save, and challenge delete so those paths
    /// can't drift apart.
    /// </summary>
    public async Task FlushAdScoreboardCacheIncludingFrozen(int gameId, CancellationToken token)
    {
        await FlushAdScoreboardCache(gameId, token);
        await RemoveAsync(CacheKey.AdScoreBoardFrozen(gameId), token);
        await RemoveAsync(CacheKey.AdTimelineFrozen(gameId), token);
        await RemoveAsync(CacheKey.KothScoreboardFrozen(gameId), token);
        await RemoveAsync(CacheKey.KothTimelineFrozen(gameId), token);
    }

    public async Task FlushRecentGamesCache(CancellationToken token) =>
        await channelWriter.WriteAsync(RecentGamesCacheHandler.MakeCacheRequest(), token);

    public async Task FlushGameListCache(CancellationToken token) =>
        await channelWriter.WriteAsync(GameListCacheHandler.MakeCacheRequest(), token);

    private static readonly MemoryCacheEntryOptions CommonMemoryCacheOptions = new()
    {
        // The pulling frequency of the scoreboard is 10s from the client side,
        // so we use memory cache to reduce the pressure on distributed cache.
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(5),
    };

    private async Task<TResult> GetOrCreateFromDistributedCacheAsync<TResult, TLogger>(
        ILogger<TLogger> logger,
        string key,
        Func<DistributedCacheEntryOptions, Task<TResult>> func,
        CancellationToken token = default)
    {
        var cacheTime = DateTimeOffset.Now;
        var value = await distributedCache.GetAsync(key, token);
        TResult? result = default;

        // most of the time, the cache is already been set
        if (TryDeserialize(value, ref result))
            return result;

        var lockKey = CacheKey.UpdateLock(key);

        // Become the sole builder, or find out someone else already is. On a
        // failed acquire, wait for the current holder to finish and re-check —
        // if they didn't produce a usable value (failed build), loop back and
        // race for the lock ourselves rather than serving nothing.
        while (!await TryAcquireLockAsync(lockKey, token))
        {
            value = await WaitForLockReleaseAsync(key, lockKey, token);
            if (TryDeserialize(value, ref result))
                return result;
        }

        int byteCount;
        try
        {
            // begin the update
            var cacheOptions = new DistributedCacheEntryOptions();
            result = await func(cacheOptions);
            var bytes = MemoryPackSerializer.Serialize(result);
            byteCount = bytes.Length;

            // finish the update
            await distributedCache.SetAsync(key, bytes, cacheOptions, token);
        }
        finally
        {
            // always release the lock
            await ReleaseLockAsync(lockKey, token);
        }

        logger.SystemLog(StaticLocalizer[
            nameof(Resources.Program.Cache_Updated),
            key, cacheTime.ToString("HH:mm:ss.fff"), byteCount
        ], TaskStatus.Success, LogLevel.Debug);

        return result;
    }

    private static bool TryDeserialize<TResult>(byte[]? value, [NotNullWhen(true)] ref TResult? result)
    {
        if (value is null)
            return false;

        try
        {
            if (MemoryPackSerializer.Deserialize<TResult>(value) is { } deserialized)
                result = deserialized;

            return result is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Try to become the sole builder for lockKey. Redis-backed: a single atomic
    /// SET-if-not-exists, so exactly one concurrent caller across any number of
    /// replicas ever wins — closes the cross-replica stampede window the old
    /// get-then-set pair left open. No Redis configured (single-instance
    /// deployment): a best-effort check-then-set — IDistributedCache has no
    /// atomic "set if absent" primitive to fall back on, but for one process a
    /// narrow double-acquire just means a harmless redundant rebuild, not
    /// corruption, so this is an acceptable, unchanged-from-before fallback.
    /// </summary>
    internal async Task<bool> TryAcquireLockAsync(string lockKey, CancellationToken token)
    {
        if (redis is not null)
            return await redis.GetDatabase().StringSetAsync(
                lockKey, RedisValue.EmptyString, TimeSpan.FromMinutes(1), When.NotExists);

        var existing = await distributedCache.GetAsync(lockKey, token);
        if (existing is not null)
            return false;

        await distributedCache.SetAsync(lockKey, [],
            new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(1) }, token);
        return true;
    }

    /// <summary>
    /// Poll until lockKey is released, then return the (hopefully now populated)
    /// value key. Detection has to match how TryAcquireLockAsync wrote the lock:
    /// when Redis is configured the lock is a raw Redis STRING (written via
    /// StringSetAsync), which IDistributedCache can't read back — it stores its
    /// OWN entries as a Redis HASH (absexp/sldexp/data fields), a different type
    /// at the same key name. Checking existence via the matching raw path avoids
    /// that mismatch.
    /// </summary>
    private async Task<byte[]?> WaitForLockReleaseAsync(string key, string lockKey, CancellationToken token)
    {
        if (redis is not null)
        {
            var db = redis.GetDatabase();
            while (await db.KeyExistsAsync(lockKey))
                await Task.Delay(100, token);
        }
        else
        {
            while (await distributedCache.GetAsync(lockKey, token) is not null)
                await Task.Delay(100, token);
        }

        return await distributedCache.GetAsync(key, token);
    }

    internal Task ReleaseLockAsync(string lockKey, CancellationToken token = default) =>
        distributedCache.RemoveAsync(lockKey, token);
}

/// <summary>
/// Cache keys
/// </summary>
public static class CacheKey
{
    /// <summary>
    /// Favicon
    /// </summary>
    public const string Favicon = "_Favicon";

    /// <summary>
    /// Index
    /// </summary>
    public const string Index = "_Index";

    /// <summary>
    /// Scoreboard
    /// </summary>
    public const string ScoreBoardBase = "_ScoreBoard";

    /// <summary>
    /// Scoreboard (frozen view, ICPC-style — built with the freeze-cutoff applied)
    /// </summary>
    public const string ScoreBoardFrozenBase = "_ScoreBoardFrozen";

    /// <summary>
    /// A&amp;D scoreboard (live) — base key for the CacheMaker handler.
    /// </summary>
    public const string AdScoreBoardBase = "_AdScoreBoard";

    /// <summary>
    /// A&amp;D score timeline (live) — base key for the CacheMaker handler.
    /// </summary>
    public const string AdTimelineBase = "_AdTimeline";

    /// <summary>
    /// King of the Hill scoreboard (live) — base key for the CacheMaker handler.
    /// </summary>
    public const string KothScoreboardBase = "_KothScoreboard";

    /// <summary>King of the Hill score timeline (live) — base key for the CacheMaker handler.</summary>
    public const string KothTimelineBase = "_KothTimeline";

    /// <summary>
    /// Recent games
    /// </summary>
    public const string RecentGames = "_RecentGames";

    /// <summary>
    /// The game list cache, latest 100 games
    /// </summary>
    public const string GameList = "_GameList";

    /// <summary>
    /// Posts
    /// </summary>
    public const string Posts = "_Posts";

    /// <summary>
    /// The cron job lock
    /// </summary>
    public const string CronJobLock = "_CronJobLock";

    /// <summary>
    /// Is exercise available
    /// </summary>
    public const string ExerciseAvailable = "_ExerciseAvailable";

    /// <summary>
    /// The client configuration
    /// </summary>
    public const string ClientConfig = "_ClientConfig";

    /// <summary>
    /// The captcha configuration
    /// </summary>
    public const string CaptchaConfig = "_CaptchaConfig";

    /// <summary>
    /// The cache update lock
    /// </summary>
    public static string UpdateLock(string key) => $"_UpdateLock{key}";

    /// <summary>
    /// The last update time
    /// </summary>
    public static string LastUpdateTime(string key) => $"_LastUpdateTime_{key}";

    /// <summary>
    /// Scoreboard cache
    /// </summary>
    public static string ScoreBoard(int id) => $"_ScoreBoard_{id}";

    /// <summary>
    /// Scoreboard cache
    /// </summary>
    public static string ScoreBoard(string id) => $"_ScoreBoard_{id}";

    /// <summary>
    /// Frozen scoreboard cache
    /// </summary>
    public static string ScoreBoardFrozen(int id) => $"_ScoreBoardFrozen_{id}";

    /// <summary>
    /// Frozen scoreboard cache
    /// </summary>
    public static string ScoreBoardFrozen(string id) => $"_ScoreBoardFrozen_{id}";

    /// <summary>A&amp;D scoreboard cache (live).</summary>
    public static string AdScoreBoard(int id) => $"_AdScoreBoard_{id}";

    /// <summary>A&amp;D scoreboard cache (live).</summary>
    public static string AdScoreBoard(string id) => $"_AdScoreBoard_{id}";

    /// <summary>A&amp;D scoreboard cache (frozen view).</summary>
    public static string AdScoreBoardFrozen(int id) => $"_AdScoreBoardFrozen_{id}";

    /// <summary>A&amp;D score timeline cache (live).</summary>
    public static string AdTimeline(int id) => $"_AdTimeline_{id}";

    /// <summary>A&amp;D score timeline cache (live).</summary>
    public static string AdTimeline(string id) => $"_AdTimeline_{id}";

    /// <summary>A&amp;D score timeline cache (frozen view).</summary>
    public static string AdTimelineFrozen(int id) => $"_AdTimelineFrozen_{id}";

    /// <summary>KotH-only scoreboard cache (live).</summary>
    public static string KothScoreboard(int id) => $"_KothScoreboard_{id}";

    /// <summary>KotH-only scoreboard cache (live).</summary>
    public static string KothScoreboard(string id) => $"_KothScoreboard_{id}";

    /// <summary>KotH-only scoreboard cache (frozen view).</summary>
    public static string KothScoreboardFrozen(int id) => $"_KothScoreboardFrozen_{id}";

    /// <summary>KotH score timeline cache (live).</summary>
    public static string KothTimeline(int id) => $"_KothTimeline_{id}";

    /// <summary>KotH score timeline cache (live).</summary>
    public static string KothTimeline(string id) => $"_KothTimeline_{id}";

    /// <summary>KotH score timeline cache (frozen view).</summary>
    public static string KothTimelineFrozen(int id) => $"_KothTimelineFrozen_{id}";

    /// <summary>
    /// Game cache
    /// </summary>
    public static string GameCache(int id) => $"_GameCache_{id}";

    /// <summary>
    /// Game notice cache
    /// </summary>
    public static string GameNotice(int id) => $"_GameNotice_{id}";

    /// <summary>
    /// Container connection counter
    /// </summary>
    public static string ConnectionCount(Guid id) => $"_Container_Conn_{id}";

    /// <summary>
    /// HashPow cache
    /// </summary>
    public static string HashPow(string key) => $"_HP_{key}";
}
