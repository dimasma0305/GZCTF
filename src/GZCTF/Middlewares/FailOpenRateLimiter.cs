using System.Threading.RateLimiting;

namespace GZCTF.Middlewares;

/// <summary>
/// Wraps a distributed (Redis-backed) rate limiter so a backend outage — connection
/// drop, timeout, command failure — fails OPEN (permits the request) instead of
/// throwing out of the ASP.NET Core rate-limiting middleware, which does not catch
/// exceptions raised by the limiter itself (both <c>CombinedAcquire</c> and
/// <c>CombinedWaitAsync</c> in <c>RateLimitingMiddleware</c> rethrow). Rate limiting
/// here is an abuse/fairness control, not a safety invariant: a transient extra
/// permit during a Redis blip is the correct trade-off against every request on the
/// platform 500ing for the same blip.
/// </summary>
sealed class FailOpenRateLimiter(System.Threading.RateLimiting.RateLimiter inner, ILogger logger)
    : System.Threading.RateLimiting.RateLimiter
{
    // A hard client-side deadline, independent of whatever SyncTimeout/ConnectTimeout
    // the underlying IConnectionMultiplexer happens to be configured with. Needed
    // because RedisSlidingWindowRateLimiter.AcquireAsyncCore never forwards the
    // CancellationToken it's given down to the actual Redis call, so passing a
    // token alone does not bound how long a stalled call can run — empirically, a
    // black-holed Redis endpoint with a 400ms StackExchange.Redis SyncTimeout still
    // took ~1s per call in practice. Racing via Task.WhenAny below gives an actual
    // upper bound on the latency this adds to every request during an outage.
    static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(300);

    static readonly RateLimitLease AllowedLease = new AlwaysAllowedLease();

    public override TimeSpan? IdleDuration
    {
        get
        {
            try
            {
                return inner.IdleDuration;
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }
    }

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        try
        {
            return inner.AttemptAcquire(permitCount);
        }
        catch (Exception e)
        {
            LogFailure(e);
            return AllowedLease;
        }
    }

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount,
        CancellationToken cancellationToken)
    {
        var acquireTask = inner.AcquireAsync(permitCount, cancellationToken).AsTask();
        var winner = await Task.WhenAny(acquireTask, Task.Delay(Deadline, cancellationToken));

        if (winner != acquireTask)
        {
            LogTimeout();
            // Don't await the loser: it may only fault well after our own deadline,
            // whenever the Redis client's own (much longer) internal timeout fires.
            // Observe its exception so it isn't reported as unobserved.
            _ = acquireTask.ContinueWith(t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            return AllowedLease;
        }

        try
        {
            return await acquireTask;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogFailure(e);
            return AllowedLease;
        }
    }

    public override RateLimiterStatistics? GetStatistics()
    {
        try
        {
            return inner.GetStatistics();
        }
        catch
        {
            return null;
        }
    }

    void LogFailure(Exception e) =>
        logger.LogWarning(e, "Distributed rate limiter backend unavailable, failing open");

    void LogTimeout() =>
        logger.LogWarning("Distributed rate limiter backend exceeded its {DeadlineMs}ms deadline, failing open",
            Deadline.TotalMilliseconds);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
    }

    protected override ValueTask DisposeAsyncCore() => inner.DisposeAsync();

    sealed class AlwaysAllowedLease : RateLimitLease
    {
        public override bool IsAcquired => true;
        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }
}
