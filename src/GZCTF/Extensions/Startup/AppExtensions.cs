using System.Reflection;
using GZCTF.Hubs;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Context;

namespace GZCTF.Extensions.Startup;

internal static class AppExtensions
{
    private static readonly StaticFileOptions DefaultStaticFileOptions = new()
    {
        OnPrepareResponse = ctx =>
        {
            ctx.Context.Response.GetTypedHeaders().CacheControl = new()
            {
                Public = true,
                MaxAge = TimeSpan.FromDays(7)
            };
        }
    };

    private static readonly WebSocketOptions DefaultWebSocketOptions =
        new() { KeepAliveInterval = TimeSpan.FromMinutes(30) };

    extension(WebApplication app)
    {
        internal async Task RunServerAsync()
        {
            await using var scope = app.Services.CreateAsyncScope();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Server>>();

            try
            {
                var version = typeof(Server).Assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()?.Description;
                logger.SystemLog(version ?? "GZ::CTF", TaskStatus.Pending, LogLevel.Debug);
                await app.RunAsync();
            }
            catch (Exception exception)
            {
                logger.LogErrorMessage(exception, StaticLocalizer[nameof(Resources.Program.Server_Failed)]);
                throw;
            }
            finally
            {
                logger.SystemLog(StaticLocalizer[nameof(Resources.Program.Server_Exited)], TaskStatus.Exit,
                    LogLevel.Debug);

                await Log.CloseAndFlushAsync();
            }
        }

        internal void UseMiddlewares()
        {
            app.UseRequestLocalization();

            app.UseResponseCaching();
            app.UseResponseCompression();

            app.UseCustomFavicon();
            app.UseStaticFiles(DefaultStaticFileOptions);

            app.UseForwardedHeaders();

            // Behind a TLS-terminating reverse proxy the container only sees http on the
            // proxy→container hop, and the proxy's source IP isn't a default "known proxy",
            // so UseForwardedHeaders won't promote the scheme. Honour X-Forwarded-Proto
            // directly here (the container is only reachable via the proxy) so generated
            // absolute URLs — OAuth redirect_uri, confirmation/reset email links — use the
            // public https origin instead of http. PublicScheme (env) forces a scheme when
            // the proxy doesn't send the header; set PublicScheme=https for an HTTPS gateway.
            var forcedScheme = app.Configuration["PublicScheme"];
            app.Use(async (context, next) =>
            {
                var proto = context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault();
                if (!string.IsNullOrEmpty(proto))
                    context.Request.Scheme = proto.Split(',')[0].Trim();
                else if (!string.IsNullOrEmpty(forcedScheme))
                    context.Request.Scheme = forcedScheme;
                await next();
            });

            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseOpenApi(options =>
                {
                    options.PostProcess += (document, _) => document.Servers.Clear();
                    options.Path = "/openapi/{documentName}.json";
                });
                // open ui in `/scalar/v1`
                app.MapScalarApiReference();
            }
            else
            {
                app.UseExceptionHandler("/error/500");
                app.UseHsts();
            }

            app.UseRouting();

            app.UseAuthentication();
            app.Use(async (context, next) =>
            {
                var fingerprint = ContextHelper.GetValidBrowserFingerprint(context.User);

                if (string.IsNullOrWhiteSpace(fingerprint))
                {
                    await next();
                    return;
                }

                using (LogContext.PushProperty("BrowserFingerprint", fingerprint))
                {
                    await next();
                }
            });
            app.UseAuthorization();

            // AFTER UseAuthentication/UseAuthorization so the partition function sees the
            // authenticated principal. Previously this ran right after UseRouting (pre-auth), so
            // context.User carried no NameIdentifier and the per-user bucket was dead — every
            // authenticated request fell through to the shared per-IP bucket (behind a reverse
            // proxy, potentially one bucket for everyone). Matches Microsoft's documented ordering.
            if (app.Configuration.GetValue<bool>("DisableRateLimit") is not true)
                app.UseRateLimiter();

            if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("RequestLogging"))
                app.UseRequestLogging();

            app.UseWebSockets(DefaultWebSocketOptions);
            app.UseTelemetry();

            app.MapHealthCheck();
            app.MapControllers();

            app.MapHub<UserHub>("/hub/user");
            app.MapHub<MonitorHub>("/hub/monitor");
            app.MapHub<AdminHub>("/hub/admin");
            app.MapHub<AttackHub>("/hub/attack");
            // Plain-WebSocket mirror of the attack feed for participant bots/overlays —
            // one JSON object per frame, no SignalR protocol. See AttackStreamService.
            app.MapGet("/hub/attack/ws",
                (HttpContext ctx, Services.AttackStreamService feed) => feed.HandleWebSocketAsync(ctx));
            app.MapHub<ContainerExecHub>("/hub/containerExec");

            app.UseIndexAsync();
        }
    }
}
