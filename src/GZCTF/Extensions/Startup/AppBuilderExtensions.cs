using Serilog;
using StackExchange.Redis;

namespace GZCTF.Extensions.Startup;

internal static class AppBuilderExtensions
{
    extension(WebApplicationBuilder builder)
    {
        internal void ConfigureWebHost()
        {
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.ConfigCustomSerializerOptions();
            });

            builder.Services.AddLocalization(options => options.ResourcesPath = "Resources")
                .Configure<RequestLocalizationOptions>(options =>
                {
                    options
                        .AddSupportedCultures(SupportedCultures)
                        .AddSupportedUICultures(SupportedCultures);

                    options.ApplyCurrentCultureToResponseHeaders = true;
                });

            builder.WebHost.ConfigureKestrel(options =>
            {
                var kestrelSection = builder.Configuration.GetSection("Kestrel");
                options.Configure(kestrelSection);
                kestrelSection.Bind(options);
            }).UseKestrel(options =>
            {
                options.ListenAnyIP(ServerPort);
                options.ListenAnyIP(MetricPort);
            });

            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Logging.AddSerilog(dispose: true);
            builder.Host.UseSerilog(dispose: true);
            builder.Configuration.AddEnvironmentVariables("GZCTF_");

            builder.Services.AddServiceDiscovery();
            builder.Services.ConfigureHttpClientDefaults(http =>
            {
                http.AddStandardResilienceHandler();
                http.AddServiceDiscovery();
                // Always send a User-Agent. .NET's HttpClient sends none by default, but some
                // upstreams reject UA-less requests — notably Discord's Cloudflare-fronted API/CDN
                // (used by the OAuth login token/userinfo calls and avatar import), which documents
                // UA-less requests as block-eligible (403 / Cloudflare error 1010).
                http.ConfigureHttpClient(c =>
                    c.DefaultRequestHeaders.UserAgent.ParseAdd("GZCTF (+https://github.com/GZTimeWalker/GZCTF)"));
            });
        }

        internal void ConfigureCacheAndSignalR()
        {
            var signalrBuilder = builder.Services.AddSignalR().AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.ConfigCustomSerializerOptions();
            });

            // Key is "Redis", matching GZCTF.AppHost's AddRedis("redis") (WithReference
            // with no name override injects ConnectionStrings__redis) and this fork's
            // docker-compose.yml / docs (ConnectionStrings__Redis). Was "RedisCache" here
            // — a stale key nothing else ever produced, so Redis silently never activated
            // and the app ran on AddDistributedMemoryCache() (in-process only, no SignalR
            // backplane) on every deployment regardless of a configured Redis.
            var connectionString = builder.Configuration.GetConnectionString("Redis");

            // Redis coordinates SIX subsystems ACROSS REPLICAS: the L2 distributed cache,
            // the SignalR backplane, the cache-rebuild lock (CacheHelper), cron leader
            // election (CronJobService), the raw-WS attack-feed fan-out (AttackStreamService),
            // and the global/login/register rate limiter. Every one of them has an in-process
            // fallback that is CORRECT and FASTER for a single instance — no network round
            // trip on the request hot path. So a single-instance deployment can opt out of
            // Redis entirely and reclaim that per-request speed, EVEN with a Redis connection
            // string still configured, by setting `SingleInstance=true`.
            //
            // Default is OFF (Redis is used whenever a connection string is present — the
            // upstream/original behavior): that way an operator who scales to >1 replica
            // WITHOUT knowing this flag still gets correct cross-replica coordination, not
            // silently-broken in-process locks. The opt-out is explicit and this fork's
            // docker-compose sets it, because the live deployment is a single instance.
            //
            // Bonus: at SingleInstance=true, Redis is never touched by the cache path, so the
            // Redis-outage failure modes (fail-open wrappers in CacheHelper/CacheMaker/Proxy)
            // simply cannot fire — they become clustered-mode-only insurance.
            var singleInstance = builder.Configuration.GetValue<bool>("SingleInstance");
            var useRedis = !singleInstance && !string.IsNullOrWhiteSpace(connectionString);

            if (!useRedis)
            {
                builder.Services.AddDistributedMemoryCache();

                if (singleInstance && !string.IsNullOrWhiteSpace(connectionString))
                    Log.Information("Cache/coordination: in-process (SingleInstance=true) — the configured " +
                        "Redis is intentionally NOT used. Unset SingleInstance to use it for horizontal scaling.");
                else
                    Log.Information("Cache/coordination: in-process (no Redis configured).");
            }
            else
            {
                builder.Services.AddStackExchangeRedisCache(options =>
                {
                    options.Configuration = connectionString;
                });

                // IDistributedCache doesn't expose the underlying connection, so a few
                // call sites (single-use-token consume, distributed lock, rate limiter)
                // need their own multiplexer to run a single atomic Redis command
                // instead of a non-atomic get-then-set pair. Registered only in Redis mode —
                // those call sites resolve it as IConnectionMultiplexer? via GetService and
                // fall back to an in-process-only-safe path when it's null (single-instance).
                // Lazy: StackExchange.Redis connects on first use of this singleton, not
                // at DI-container-build time.
                builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
                    ConnectionMultiplexer.Connect(connectionString));

                signalrBuilder.AddStackExchangeRedis(connectionString, options =>
                {
                    options.Configuration.ChannelPrefix = new RedisChannel("GZCTF", RedisChannel.PatternMode.Literal);
                });

                Log.Information("Cache/coordination: Redis (clustered mode) — cross-replica cache, " +
                    "SignalR backplane, locks, leader election, and rate limiting active.");
            }

            builder.Services.AddMemoryCache();
        }
    }
}
