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
        PowChallenge,

        /// <summary>
        /// Password-login brute-force limit. Unlike the other named policies this
        /// one is per-IP partitioned (not a single global bucket) and Redis-backed
        /// when available — see the Login registration below.
        /// </summary>
        Login
    }

    const int GlobalPermitLimit = 150;
    static readonly TimeSpan GlobalWindow = TimeSpan.FromMinutes(1);

    // Per-IP throttle for POST /api/Account/LogIn. Identity's own lockoutOnFailure is
    // intentionally left off (usernames are public on the scoreboard, so a per-account
    // lock is a trivial mid-game DoS against rivals), so this per-IP cap is the actual
    // brute-force ceiling against the 6-char-min password policy. Set to 50/min (not a
    // tight 10): at a shared-NAT venue — campus LAN / conference WiFi / CGNAT — dozens of
    // legitimate players share ONE public IP and all log in at kickoff, and a 10/min cap
    // would 429-lock the venue. 50/min still shuts down an automated brute-forcer (which
    // does thousands/min) while tolerating a real crowd. NOTE: this counts every attempt,
    // not just failures; counting only failed logins would be the tighter-and-fairer fix.
    const int LoginPermitLimit = 50;
    static readonly TimeSpan LoginWindow = TimeSpan.FromMinutes(1);

    // Per-IP throttle for the mail-triggering endpoints (Register / Recovery / ChangeEmail).
    // Per-recipient flooding is already covered independently by IMailRateLimiter, so this
    // is purely per-IP abuse limiting — and per-IP (not one global bucket) so a signup rush
    // from many players can't exhaust a single platform-wide allowance and lock everyone out.
    const int RegisterPermitLimit = 20;
    static readonly TimeSpan RegisterWindow = TimeSpan.FromMinutes(5);

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
                return GetIpPartition(userId, redis, logger, GlobalPermitLimit, GlobalWindow, 60);

            var ipKey = NormalizedClientIpKey(context);
            if (ipKey is null)
                return RateLimitPartition.GetNoLimiter(IPAddress.Loopback.ToString());

            return GetIpPartition(ipKey, redis, logger, GlobalPermitLimit, GlobalWindow, 60);
        });
        // Per-IP brute-force throttle for the login endpoint (see LimitPolicy.Login).
        // Distinct Redis key namespace ("login:") from the global limiter so the two
        // counters don't share a bucket. Loopback is exempt (health probes / same-host
        // tooling), matching the global limiter.
        options.AddPolicy(nameof(LimitPolicy.Login), context =>
        {
            var ipKey = NormalizedClientIpKey(context);
            return ipKey is null
                ? RateLimitPartition.GetNoLimiter("login-loopback")
                : GetIpPartition($"login:{ipKey}", redis, logger, LoginPermitLimit, LoginWindow, 0);
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
        // Per-IP (was a single unpartitioned global bucket): otherwise a kickoff signup /
        // forgot-password rush from many DIFFERENT players collectively exhausts one ~20/150s
        // allowance and 429s every new registrant AND every password reset platform-wide.
        options.AddPolicy(nameof(LimitPolicy.Register), context =>
        {
            var ipKey = NormalizedClientIpKey(context);
            return ipKey is null
                ? RateLimitPartition.GetNoLimiter("register-loopback")
                : GetIpPartition($"register:{ipKey}", redis, logger, RegisterPermitLimit, RegisterWindow, 10);
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
    /// Normalizes the caller's IP into a partition key: IPv4-mapped IPv6
    /// (::ffff:a.b.c.d) is folded to bare IPv4 so a dual-stack client can't get two
    /// separate buckets and double an IP-keyed limit (matches the IP-attribution
    /// helpers). Returns null for loopback / no address — the caller treats that as
    /// "no limit" (health probes, same-host tooling).
    /// </summary>
    static string? NormalizedClientIpKey(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is not null && address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address is null || IPAddress.IsLoopback(address))
            return null;
        return address.ToString();
    }

    /// <summary>
    /// Redis-backed when available (one shared counter per key across every
    /// replica); otherwise the original in-process sliding window. QueueLimit/
    /// QueueProcessingOrder/SegmentsPerWindow have no Redis equivalent here — the
    /// distributed path is a single atomic allow/deny check per request rather than
    /// a locally queued one, which is the correct shape for a shared counter.
    /// </summary>
    static RateLimitPartition<string> GetIpPartition(string key, IConnectionMultiplexer? redis, ILogger logger,
        int permitLimit, TimeSpan window, int fallbackQueueLimit) =>
        redis is not null
            ? RateLimitPartition.Get(key, k => new FailOpenRateLimiter(
                new RedisSlidingWindowRateLimiter<string>(k, new RedisSlidingWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window,
                    ConnectionMultiplexerFactory = () => redis
                }), logger))
            : RateLimitPartition.GetSlidingWindowLimiter(key,
                _ => new()
                {
                    QueueLimit = fallbackQueueLimit,
                    PermitLimit = permitLimit,
                    Window = window,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    SegmentsPerWindow = 6
                });
}
