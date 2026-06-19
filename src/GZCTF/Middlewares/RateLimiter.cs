using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Mime;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;

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

    public static void ConfigureRateLimiter(RateLimiterOptions options)
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
                return RateLimitPartition.GetSlidingWindowLimiter(userId,
                    _ => new()
                    {
                        QueueLimit = 60,
                        PermitLimit = 150,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        SegmentsPerWindow = 6
                    });

            var address = context.Connection.RemoteIpAddress;

            // Normalize IPv4-mapped IPv6 (::ffff:a.b.c.d) to the bare IPv4 form so a
            // dual-stack client can't get two separate buckets (e.g. 192.168.1.1 vs
            // ::ffff:192.168.1.1) and double an IP-keyed limit (register/recovery).
            // Matches the normalization already done in the IP-attribution helpers.
            if (address is not null && address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();

            if (address is null || IPAddress.IsLoopback(address))
                return RateLimitPartition.GetNoLimiter(IPAddress.Loopback.ToString());

            return RateLimitPartition.GetSlidingWindowLimiter(address.ToString(),
                _ => new()
                {
                    QueueLimit = 60,
                    PermitLimit = 150,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    SegmentsPerWindow = 6
                });
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
}
