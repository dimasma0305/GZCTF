using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace GZCTF.Services;

/// <summary>
/// Per-recipient throttle for account mail (email confirmation / password reset /
/// email change). The endpoints that trigger these already carry the global
/// <c>Register</c> rate-limit policy, but that is a single unpartitioned bucket:
/// it bounds total outbound volume but does nothing to stop one attacker
/// email-bombing a single victim's inbox (repeatedly POSTing Recovery/ChangeEmail
/// with the victim's address). This caps the number of mails any one address can
/// receive per window, independent of the global limiter.
/// </summary>
public interface IMailRateLimiter
{
    /// <summary>
    /// Counts one send attempt against <paramref name="email"/> and returns whether
    /// it is under the per-recipient cap. Fails OPEN: a cache/Redis error never
    /// blocks a legitimate signup / password recovery.
    /// </summary>
    Task<bool> TryAcquireAsync(string? email, CancellationToken token = default);
}

public sealed class MailRateLimiter(IDistributedCache cache, IConnectionMultiplexer? redis = null) : IMailRateLimiter
{
    // At most MaxPerWindow account mails to any single recipient per Window. Generous
    // for real use (a person rarely needs >5 verification/reset mails in 10 minutes)
    // while capping inbox-flooding.
    const int MaxPerWindow = 5;
    static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    public async Task<bool> TryAcquireAsync(string? email, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            return true;

        var key = $"mailrate:{email.Trim().ToLowerInvariant()}";

        try
        {
            if (redis is not null)
            {
                // Atomic across replicas: INCR returns the post-increment count; set the
                // window TTL only on the first hit so the window is fixed, not sliding.
                var db = redis.GetDatabase();
                var count = await db.StringIncrementAsync(key);
                if (count == 1)
                    await db.KeyExpireAsync(key, Window);
                return count <= MaxPerWindow;
            }

            // Single-instance fallback (Redis not configured): IDistributedCache-backed
            // fixed window. Not atomic under concurrency, but adequate for one replica.
            var raw = await cache.GetStringAsync(key, token);
            var current = int.TryParse(raw, out var c) ? c : 0;
            if (current >= MaxPerWindow)
                return false;
            await cache.SetStringAsync(key, (current + 1).ToString(),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = Window }, token);
            return true;
        }
        catch
        {
            // Fail open — mail delivery for signup / recovery must not depend on the
            // throttle's backing store being healthy.
            return true;
        }
    }
}
