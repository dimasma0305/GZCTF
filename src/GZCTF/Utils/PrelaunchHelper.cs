using GZCTF.Models.Internal;
using GZCTF.Services.Cache;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace GZCTF.Utils;

public static class PrelaunchHelper
{
    extension(WebApplication app)
    {
        public async Task RunPrelaunchWorkAsync()
        {
            using var serviceScope = app.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();

            var logger = serviceScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            var context = serviceScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cache = serviceScope.ServiceProvider.GetRequiredService<IDistributedCache>();

            if (app.Configuration["xorKey"] is not { Length: > 0 })
                ExitWithFatalMessage(StaticLocalizer[nameof(Resources.Program.Init_XorKeyNotSet)]);

            await MigrateUnderAdvisoryLockAsync(context);

            if (!await context.Posts.AnyAsync())
            {
                await context.Posts.AddAsync(new()
                {
                    UpdateTimeUtc = DateTimeOffset.UtcNow,
                    Title = StaticLocalizer[nameof(Resources.Program.Init_PostTitle)],
                    Summary = StaticLocalizer[nameof(Resources.Program.Init_PostSummary)],
                    Content = StaticLocalizer[nameof(Resources.Program.Init_PostContent)]
                });

                await context.SaveChangesAsync();
            }

            if (app.Environment.IsDevelopment() || app.Configuration.GetSection("ADMIN_PASSWORD").Exists())
            {
                var userManager =
                    serviceScope.ServiceProvider.GetRequiredService<UserManager<UserInfo>>();
                var admin = await userManager.FindByNameAsync("Admin");
                var password = app.Environment.IsDevelopment()
                    ? "Admin@2022"
                    : app.Configuration.GetValue<string>("ADMIN_PASSWORD");

                if (admin is null && password is not null)
                {
                    admin = new UserInfo
                    {
                        UserName = "Admin",
                        Email = "admin@example.invalid",
                        Role = Role.Admin,
                        EmailConfirmed = true,
                        RegisterTimeUtc = DateTimeOffset.UtcNow
                    };

                    var result = await userManager.CreateAsync(admin, password);
                    if (!result.Succeeded)
                        logger.SystemLog(
                            StaticLocalizer[nameof(Resources.Program.Init_AdminCreationFailed),
                                result.Errors.FirstOrDefault()?.Description ?? "null"], TaskStatus.Failed,
                            LogLevel.Debug);
                }
            }

            var containerConfig =
                serviceScope.ServiceProvider.GetRequiredService<IOptions<ContainerProvider>>();
            if (containerConfig.Value.EnableTrafficCapture &&
                containerConfig.Value.PortMappingType != ContainerPortMappingType.PlatformProxy)
                logger.SystemLog(StaticLocalizer[nameof(Resources.Program.Init_CaptureNotAvailable)],
                    TaskStatus.Failed, LogLevel.Warning);

            if (!cache.CacheCheck(logger))
                ExitWithFatalMessage(StaticLocalizer[nameof(Resources.Program.Init_InvalidCacheConfig)]);

            await cache.RemoveAsync(CacheKey.Index);
            await cache.RemoveAsync(CacheKey.ClientConfig);
            await cache.RemoveAsync(CacheKey.CaptchaConfig);

            var defaultRules = Services.SuspicionService.DefaultRules;
            if (!await context.SuspicionRules.AnyAsync())
            {
                await context.SuspicionRules.AddRangeAsync(defaultRules);
                await context.SaveChangesAsync();
            }
            else
            {
                var existingRuleCodes = await context.SuspicionRules.Select(r => r.RuleCode).ToListAsync();
                var newRules = defaultRules.Where(r => !existingRuleCodes.Contains(r.RuleCode)).ToList();

                if (newRules.Count != 0)
                {
                    await context.SuspicionRules.AddRangeAsync(newRules);
                    await context.SaveChangesAsync();
                }
            }

            if (app.Environment.IsDevelopment())
                await DevDataSeeder.SeedAsync(serviceScope.ServiceProvider, logger, CancellationToken.None);
        }
    }

    /// <summary>
    /// Run EF migrations under a Postgres <b>session</b> advisory lock so two replicas
    /// starting together can't race the DDL. Every replica takes the SAME lock before
    /// migrating: exactly one runs the migration while the others block, then acquire, find
    /// it already applied (no-op), and release. The lock is held on one explicitly-opened
    /// connection for the whole critical section (EF reuses an already-open connection and
    /// won't close it). Postgres-only; a non-Npgsql provider (unit tests) just migrates.
    /// </summary>
    private static async Task MigrateUnderAdvisoryLockAsync(AppDbContext context)
    {
        if (!context.Database.IsNpgsql())
        {
            if (context.Database.GetMigrations().Any())
                await context.Database.MigrateAsync();
            await context.Database.EnsureCreatedAsync();
            return;
        }

        var conn = context.Database.GetDbConnection();
        var wasOpen = conn.State == System.Data.ConnectionState.Open;
        if (!wasOpen)
            await conn.OpenAsync();

        var locked = false;
        try
        {
            await ExecScalarAsync(conn, "SELECT pg_advisory_lock(hashtext('gzctf_migration'))");
            locked = true;

            if (context.Database.GetMigrations().Any())
                await context.Database.MigrateAsync();

            await context.Database.EnsureCreatedAsync();
        }
        finally
        {
            if (locked)
                try { await ExecScalarAsync(conn, "SELECT pg_advisory_unlock(hashtext('gzctf_migration'))"); }
                catch { /* the lock auto-releases when this session/connection closes anyway */ }
            if (!wasOpen)
                await conn.CloseAsync();
        }
    }

    private static async Task ExecScalarAsync(System.Data.Common.DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    extension(IDistributedCache cache)
    {
        private bool CacheCheck(ILogger<Program> logger)
        {
            var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
            var cacheVersion = $"GZCTF@{version}";

            try
            {
                cache.SetString("_ValidCheck", cacheVersion);
                return cache.GetString("_ValidCheck") == cacheVersion;
            }
            catch (Exception e)
            {
                logger.LogErrorMessage(e, StaticLocalizer[nameof(Resources.Program.Init_InvalidCacheConfig)]);
                return false;
            }
        }
    }
}
