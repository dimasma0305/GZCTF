using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Mime;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;
using RedisRateLimiting;
using StackExchange.Redis;

namespace GZCTF.Middlewares;

/// <summary>
/// The rate limiter middleware
/// </summary>
[ExcludeFromCodeCoverage]
public static class RateLimiter
{
    public enum LimitPolicy
    {
        /// <summary>
        /// Concurrency operation limit
        /// </summary>
        Concurrency,

        /// <summary>
        /// Register limit
        /// </summary>
        Register,

        /// <summary>
        /// Database query limit
        /// </summary>
        Query,

        /// <summary>
        /// Container operation limit
        /// </summary>
        Container,

        /// <summary>
        /// Flag submit limit
        /// </summary>
        Submit,

        /// <summary>
        /// Pow challenge generation limit
        /// </summary>
        PowChallenge
    }

    const int GlobalPermitLimit = 150;
    static readonly TimeSpan GlobalWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Configures ASP.NET Core's rate limiter. Scoped only to the GlobalLimiter
    /// (the per-user/per-IP sliding window every request goes through): under
    /// horizontal scaling with a non-sticky load balancer, each replica previously
    /// kept an independent in-memory counter per user/IP, so a client hitting N
    /// replicas effectively got up to N times the intended rate. When
    /// <paramref name="redis"/> is available, the same limit is now enforced via a
    /// single Redis-backed counter shared by every replica (a Lua sliding-window-log
    /// script, one round trip per check — see RedisRateLimiting), wrapped in
    /// <see cref="FailOpenRateLimiter"/> so a Redis outage degrades to "unlimited"
    /// rather than 500ing every request. Falls back to the original in-memory
    /// limiter when Redis isn't configured (single-instance deployments, local dev).
    /// <para/>
    /// The six named policies below (<see cref="LimitPolicy"/>) are NOT part of this
    /// fix — deliberately deferred. Unlike the GlobalLimiter, ASP.NET Core's
    /// AddSlidingWindowLimiter/AddTokenBucketLimiter/AddConcurrencyLimiter register
    /// each policy as ONE bucket shared by every caller (no partition key), so they
    /// are already a single global limit even on one instance; going distributed
    /// would only matter once GZCTF actually runs >1 replica, at which point they
    /// should move to the same Redis-backed pattern used here.
    /// </summary>
    public static void ConfigureRateLimiter(RateLimiterOptions options, IConnectionMultiplexer? redis,
        ILogger logger)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            // East-west A&D SSH bastion endpoints (api/Internal/Ad/Ssh) are called
            // by the jump-host sidecar from a SINGLE IP and carry no user claim, so
            // they'd otherwise all share ONE IP-keyed bucket — letting one
            // attacker's pubkey-probe flood 429-lock out SSH auth platform-wide
            // (lookups run pre-signature via AuthorizedKeysCommand). They're already
            // gated by the shared internal secret, so exempt them from the limiter.
            if (context.Request.Path.StartsWithSegments("/api/Internal/Ad/Ssh"))
                return RateLimitPartition.GetNoLimiter("internal-ad-ssh");

            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (userId is not null)
                return GetGlobalPartition(userId, redis, logger);

            var address = context.Connection.RemoteIpAddress;

            // Normalize IPv4-mapped IPv6 (::ffff:a.b.c.d) to the bare IPv4 form so a
            // dual-stack client can't get two separate buckets (e.g. 192.168.1.1 vs
            // ::ffff:192.168.1.1) and double an IP-keyed limit (register/recovery).
            // Matches the normalization already done in the IP-attribution helpers.
            if (address is not null && address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            if (address is null || IPAddress.IsLoopback(address))
                return RateLimitPartition.GetNoLimiter(IPAddress.Loopback.ToString());

            return GetGlobalPartition(address.ToString(), redis, logger);
        });
        options.OnRejected = async (context, cancellationToken) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.HttpContext.Response.ContentType = MediaTypeNames.Application.Json;

            var localizer =
                context.HttpContext.RequestServices.GetRequiredService<IStringLocalizer<Program>>();
            var afterSec = (int)TimeSpan.FromMinutes(1).TotalSeconds;

            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                afterSec = (int)retryAfter.TotalSeconds;

            context.HttpContext.Response.Headers.RetryAfter = afterSec.ToString(NumberFormatInfo.InvariantInfo);
            await context.HttpContext.Response.WriteAsJsonAsync(
                new RequestResponse(localizer[nameof(Resources.Program.RateLimit_TooManyRequests), afterSec],
                    StatusCodes.Status429TooManyRequests
                ), cancellationToken);
        };
        options.AddConcurrencyLimiter(nameof(LimitPolicy.Concurrency), o =>
        {
            o.PermitLimit = 1;
            o.QueueLimit = 20;
        });
        options.AddSlidingWindowLimiter(nameof(LimitPolicy.Register), o =>
        {
            o.QueueLimit = 10;
            o.PermitLimit = 20;
            o.Window = TimeSpan.FromSeconds(150);
            o.QueueProcessingOrder = QueueProcessingOrder.NewestFirst;
            o.SegmentsPerWindow = 5;
        });
        options.AddTokenBucketLimiter(nameof(LimitPolicy.Query), o =>
        {
            o.TokenLimit = 100;
            o.TokensPerPeriod = 10;
            o.ReplenishmentPeriod = TimeSpan.FromSeconds(10);
        });
        options.AddTokenBucketLimiter(nameof(LimitPolicy.PowChallenge), o =>
        {
            o.TokenLimit = 40;
            o.TokensPerPeriod = 5;
            o.ReplenishmentPeriod = TimeSpan.FromSeconds(30);
        });
        options.AddTokenBucketLimiter(nameof(LimitPolicy.Container), o =>
        {
            o.TokenLimit = 120;
            o.TokensPerPeriod = 30;
            o.ReplenishmentPeriod = TimeSpan.FromSeconds(10);
        });
        options.AddTokenBucketLimiter(nameof(LimitPolicy.Submit), o =>
        {
            o.TokenLimit = 100;
            o.TokensPerPeriod = 50;
            o.ReplenishmentPeriod = TimeSpan.FromSeconds(5);
        });
    }

    /// <summary>
    /// Redis-backed when available (one shared counter per key across every
    /// replica); otherwise the original in-process sliding window. QueueLimit/
    /// QueueProcessingOrder/SegmentsPerWindow have no Redis equivalent here — the
    /// distributed path is a single atomic allow/deny check per request rather than
    /// a locally queued one, which is the correct shape for a shared counter.
    /// </summary>
    static RateLimitPartition<string> GetGlobalPartition(string key, IConnectionMultiplexer? redis, ILogger logger) =>
        redis is not null
            ? RateLimitPartition.Get(key, k => new FailOpenRateLimiter(
                new RedisSlidingWindowRateLimiter<string>(k, new RedisSlidingWindowRateLimiterOptions
                {
                    PermitLimit = GlobalPermitLimit,
                    Window = GlobalWindow,
                    ConnectionMultiplexerFactory = () => redis
                }), logger))
            : RateLimitPartition.GetSlidingWindowLimiter(key,
                _ => new()
                {
                    QueueLimit = 60,
                    PermitLimit = GlobalPermitLimit,
                    Window = GlobalWindow,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    SegmentsPerWindow = 6
                });
}
