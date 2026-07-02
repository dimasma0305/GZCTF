using Azure.Monitor.OpenTelemetry.AspNetCore;
using GZCTF.Models.Internal;
using GZCTF.Services.HealthCheck;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace GZCTF.Extensions.Startup;

public static class TelemetryExtension
{
    internal static TelemetryConfig? TelemetryConfig;

    extension(WebApplicationBuilder builder)
    {
        public void ConfigureTelemetry()
        {
            builder.Services.AddHealthChecks()
                .AddApplicationLifecycleHealthCheck()
                .AddCheck<StorageHealthCheck>("Storage")
                .AddCheck<CacheHealthCheck>("Cache")
                .AddCheck<DatabaseHealthCheck>("Database");

            TelemetryConfig = builder.Configuration.GetSection("Telemetry").Get<TelemetryConfig>();

            if (TelemetryConfig is not { Enable: true })
                return;

            builder.Services.AddTelemetryHealthCheckPublisher();
            var otl = builder.Services.AddOpenTelemetry();

            otl.ConfigureResource(resource =>
                resource.AddService("GZCTF",
                    serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString(3)));

            otl.WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation();
                metrics.AddHttpClientInstrumentation();
                metrics.AddRuntimeInstrumentation();
                metrics.AddProcessInstrumentation();
                metrics.AddNpgsqlInstrumentation();
                metrics.AddAWSInstrumentation();
                metrics.AddMeter("Microsoft.Extensions.Diagnostics.HealthChecks");

                if (TelemetryConfig is { Prometheus.Enable: true })
                    metrics.AddPrometheusExporter(options =>
                    {
                        options.DisableTotalNameSuffixForCounters =
                            !TelemetryConfig.Prometheus.TotalNameSuffixForCounters;
                    });

                if (TelemetryConfig is { Console.Enable: true })
                    metrics.AddConsoleExporter();
            });

            otl.WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation();
                tracing.AddHttpClientInstrumentation();
                tracing.AddEntityFrameworkCoreInstrumentation();
                tracing.AddRedisInstrumentation();
                tracing.AddNpgsql();
                tracing.AddAWSInstrumentation();
                tracing.AddGrpcClientInstrumentation();

                if (TelemetryConfig is { Console.Enable: true })
                    tracing.AddConsoleExporter();
            });

            if (TelemetryConfig is { AzureMonitor.Enable: true })
                otl.UseAzureMonitor(options =>
                    options.ConnectionString = TelemetryConfig.AzureMonitor.ConnectionString);

            if (TelemetryConfig is not { OpenTelemetry: { Enable: true, EndpointUri: var uri } })
                return;

            builder.Logging.AddOpenTelemetry(options =>
            {
                options.IncludeScopes = true;
                options.IncludeFormattedMessage = true;
                options.ParseStateValues = true;
            });

            if (uri is null)
                otl.UseOtlpExporter();
            else
                otl.UseOtlpExporter(TelemetryConfig.OpenTelemetry.Protocol, new(uri));
        }
    }

    extension(WebApplication app)
    {
        public void MapHealthCheck()
        {
            // Liveness: run NO checks — a 200 just means the process is up and serving
            // requests. A dependency being down (Postgres/Redis/storage) must NOT fail
            // liveness, or an orchestrator would kill+restart a pod that a restart can't
            // fix, turning a transient DB blip into a crash loop. (Kept at "/healthz" for
            // backward compatibility with existing liveness probes / the compose healthcheck.)
            app.MapHealthChecks("/healthz",
                    new HealthCheckOptions { Predicate = _ => false })
                .DisableHttpMetrics()
                .AddEndpointFilter(MetricPortOnly);

            // Readiness: run every check (app-lifecycle + Storage + Cache + Database) so a
            // dependency outage or a graceful-shutdown drain takes this instance out of the
            // load-balancer rotation WITHOUT killing it. Point k8s readinessProbe here.
            app.MapHealthChecks("/readyz")
                .DisableHttpMetrics()
                .AddEndpointFilter(MetricPortOnly);
        }

        // Both health endpoints are exposed only on the internal metrics port (never the
        // public web port), so probe/scrape traffic can't be reached by participants.
        private static async ValueTask<object?> MetricPortOnly(
            EndpointFilterInvocationContext context, EndpointFilterDelegate next)
            => context.HttpContext.Connection.LocalPort == MetricPort
                ? await next(context)
                : Results.NotFound();
    }

    extension(IApplicationBuilder appBuilder)
    {
        public void UseTelemetry()
        {
            if (TelemetryConfig is not { Prometheus.Enable: true })
                return;

            appBuilder.UseOpenTelemetryPrometheusScrapingEndpoint(context
                => context.Connection.LocalPort == MetricPort
                   && string.Equals(
                       context.Request.Path.ToString().TrimEnd('/'),
                       "/metrics",
                       StringComparison.OrdinalIgnoreCase));
        }
    }
}
