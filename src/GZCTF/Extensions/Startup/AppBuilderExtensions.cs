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

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                builder.Services.AddDistributedMemoryCache();
            }
            else
            {
                builder.Services.AddStackExchangeRedisCache(options =>
                {
                    options.Configuration = connectionString;
                });

                signalrBuilder.AddStackExchangeRedis(connectionString, options =>
                {
                    options.Configuration.ChannelPrefix = new RedisChannel("GZCTF", RedisChannel.PatternMode.Literal);
                });
            }

            builder.Services.AddMemoryCache();
        }
    }
}
