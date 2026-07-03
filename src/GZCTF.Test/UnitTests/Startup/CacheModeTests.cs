using System.Collections.Generic;
using GZCTF.Extensions.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace GZCTF.Test.UnitTests.Startup;

/// <summary>
/// Pins the SingleInstance opt-out: with a Redis connection string configured, the flag
/// must decide whether the six Redis-coordinated subsystems run against Redis (clustered)
/// or in-process (single instance). The single-instance path must NOT register
/// IConnectionMultiplexer (so CacheHelper / CronJob / AttackStreamService / the rate limiter
/// all take their in-process fallback) and must use the in-memory IDistributedCache — i.e.
/// zero Redis on the request hot path.
/// </summary>
public class CacheModeTests
{
    private static WebApplicationBuilder BuildBuilder(bool singleInstance, string? redisConn)
    {
        var settings = new Dictionary<string, string?>();
        if (redisConn is not null)
            settings["ConnectionStrings:Redis"] = redisConn;
        if (singleInstance)
            settings["SingleInstance"] = "true";

        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.ConfigureCacheAndSignalR();
        return builder;
    }

    private static ServiceProvider BuildServices(bool singleInstance, string? redisConn) =>
        BuildBuilder(singleInstance, redisConn).Services.BuildServiceProvider();

    [Fact]
    public void SingleInstance_WithRedisConfigured_UsesInProcess_NoMultiplexer()
    {
        using var sp = BuildServices(singleInstance: true, redisConn: "localhost:6379");

        // The whole point of the opt-out: Redis is present but deliberately unused.
        Assert.Null(sp.GetService<IConnectionMultiplexer>());
        var cache = sp.GetService<IDistributedCache>();
        Assert.NotNull(cache);
        Assert.DoesNotContain("Redis", cache!.GetType().Name);
    }

    [Fact]
    public void ClusteredMode_WithRedisConfigured_WiresRedis()
    {
        var builder = BuildBuilder(singleInstance: false, redisConn: "localhost:6379");

        // Default (no opt-out) + a Redis string => Redis is wired for cross-replica coordination.
        // Assert the multiplexer is REGISTERED (don't resolve it — the factory connects eagerly,
        // and no Redis runs in the test env). IDistributedCache resolves to the Redis cache, which
        // connects lazily on first use, so constructing it here is safe.
        Assert.Contains(builder.Services, d => d.ServiceType == typeof(IConnectionMultiplexer));
        using var sp = builder.Services.BuildServiceProvider();
        var cache = sp.GetService<IDistributedCache>();
        Assert.NotNull(cache);
        Assert.Contains("Redis", cache!.GetType().Name);
    }

    [Fact]
    public void NoRedisConfigured_UsesInProcess_RegardlessOfFlag()
    {
        using var sp = BuildServices(singleInstance: false, redisConn: null);

        Assert.Null(sp.GetService<IConnectionMultiplexer>());
        var cache = sp.GetService<IDistributedCache>();
        Assert.NotNull(cache);
        Assert.DoesNotContain("Redis", cache!.GetType().Name);
    }
}
