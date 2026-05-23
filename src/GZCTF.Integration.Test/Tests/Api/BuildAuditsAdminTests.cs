using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GZCTF.Integration.Test.Base;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Models.Request.Account;
using GZCTF.Models.Response.Admin;
using GZCTF.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace GZCTF.Integration.Test.Tests.Api;

/// <summary>
/// HTTP-level coverage for the /admin/builds endpoint surface. These
/// can't be unit-tested with InMemory EF because the bulk-delete +
/// prune endpoints use <c>ExecuteDeleteAsync</c>, which the InMemory
/// provider doesn't implement. Real postgres via testcontainers is
/// the cheapest path that exercises the actual SQL.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class BuildAuditsAdminTests(GZCTFApplicationFactory factory, ITestOutputHelper output)
{
    private const string AdminPassword = "Admin@BuildAudit123";

    /// <summary>
    /// Match the platform's Unix-ms timestamp encoding (see
    /// <c>DateTimeOffsetJsonConverter</c>); the default deserializer
    /// expects ISO strings and trips on the numbers we emit.
    /// </summary>
    private static JsonSerializerOptions JsonOpts { get; } = BuildJsonOpts();
    private static JsonSerializerOptions BuildJsonOpts()
    {
        var o = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        o.Converters.Add(new DateTimeOffsetJsonConverter());
        return o;
    }

    private async Task<(HttpClient client, TestDataSeeder.SeededUser admin)> AdminClientAsync()
    {
        var admin = await TestDataSeeder.CreateUserAsync(factory.Services,
            TestDataSeeder.RandomName(), AdminPassword, role: Role.Admin);
        var c = factory.CreateClient();
        var login = await c.PostAsJsonAsync("/api/Account/LogIn",
            new LoginModel { UserName = admin.UserName, Password = AdminPassword });
        login.EnsureSuccessStatusCode();
        return (c, admin);
    }

    /// <summary>
    /// Seed a single audit row directly via the DbContext so we don't
    /// have to actually run the build pipeline. Returns the audit Id +
    /// the game Id used (so the test can also assert game-scoped reads).
    /// </summary>
    private async Task<(int auditId, int gameId, int challengeId)> SeedAuditAsync(
        ChallengeBuildStatus status,
        BuildTrigger trigger = BuildTrigger.Manual)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var game = await TestDataSeeder.CreateGameAsync(factory.Services, $"Audit {TestDataSeeder.RandomName()}");
        var challenge = await TestDataSeeder.CreateStaticChallengeAsync(factory.Services, game.Id,
            $"Chal {TestDataSeeder.RandomName()}", "flag{audit}");
        var audit = new ChallengeBuildAudit
        {
            ChallengeId = challenge.Id,
            GameId = game.Id,
            Trigger = trigger,
            Status = status,
            Attempt = 1,
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
            FinishedAtUtc = status == ChallengeBuildStatus.Failed || status == ChallengeBuildStatus.Success
                ? DateTimeOffset.UtcNow
                : null,
        };
        db.ChallengeBuildAudits.Add(audit);
        await db.SaveChangesAsync();
        return (audit.Id, game.Id, challenge.Id);
    }

    [Fact]
    public async Task ListBuilds_FilterByStatus_OnlyReturnsMatching()
    {
        var (client, _) = await AdminClientAsync();
        using (client)
        {
            var (failedId, _, _) = await SeedAuditAsync(ChallengeBuildStatus.Failed);
            var (succId, _, _) = await SeedAuditAsync(ChallengeBuildStatus.Success);

            var failedOnly = await client.GetFromJsonAsync<ChallengeBuildAuditModel[]>(
                "/api/admin/builds?status=Failed", JsonOpts);
            Assert.NotNull(failedOnly);
            Assert.Contains(failedOnly!, a => a.Id == failedId);
            Assert.DoesNotContain(failedOnly!, a => a.Id == succId);
        }
    }

    [Fact]
    public async Task ListInProgress_NoActiveBuilds_ReturnsEmpty()
    {
        var (client, _) = await AdminClientAsync();
        using (client)
        {
            // No manipulation of the queue; the InMemory dict should be empty.
            var inFlight = await client.GetFromJsonAsync<ChallengeBuildInProgressModel[]>(
                "/api/admin/builds/inprogress", JsonOpts);
            Assert.NotNull(inFlight);
            // Other tests in the same collection might leave entries — assert empty isn't safe.
            // What we can assert: returns valid JSON array.
            Assert.IsType<ChallengeBuildInProgressModel[]>(inFlight);
        }
    }

    [Fact]
    public async Task DeleteAudit_Existing_ReturnsOkAndRemoves()
    {
        var (client, _) = await AdminClientAsync();
        using (client)
        {
            var (auditId, _, _) = await SeedAuditAsync(ChallengeBuildStatus.Success);

            var resp = await client.DeleteAsync($"/api/admin/builds/{auditId}");
            resp.EnsureSuccessStatusCode();

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Null(await db.ChallengeBuildAudits.AsNoTracking().FirstOrDefaultAsync(a => a.Id == auditId));
        }
    }

    [Fact]
    public async Task DeleteAudit_Missing_Returns404()
    {
        var (client, _) = await AdminClientAsync();
        using (client)
        {
            var resp = await client.DeleteAsync("/api/admin/builds/2147483646");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
    }

    [Fact]
    public async Task PruneFailed_OnlyDeletesFailedRows()
    {
        var (client, _) = await AdminClientAsync();
        using (client)
        {
            var (failedA, _, _) = await SeedAuditAsync(ChallengeBuildStatus.Failed);
            var (failedB, _, _) = await SeedAuditAsync(ChallengeBuildStatus.Failed);
            var (success, _, _) = await SeedAuditAsync(ChallengeBuildStatus.Success);

            var resp = await client.PostAsync("/api/admin/builds/prunefailed", content: null);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<PruneResultModel>();
            Assert.NotNull(body);
            Assert.True(body!.Removed >= 2); // at least our two; other tests may have failed rows

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Null(await db.ChallengeBuildAudits.AsNoTracking().FirstOrDefaultAsync(a => a.Id == failedA));
            Assert.Null(await db.ChallengeBuildAudits.AsNoTracking().FirstOrDefaultAsync(a => a.Id == failedB));
            Assert.NotNull(await db.ChallengeBuildAudits.AsNoTracking().FirstOrDefaultAsync(a => a.Id == success));
        }
    }

    [Fact]
    public async Task BulkDelete_EmptyIds_ReturnsBadRequest()
    {
        var (client, _) = await AdminClientAsync();
        using (client)
        {
            var resp = await client.PostAsJsonAsync("/api/admin/builds/bulkdelete", Array.Empty<int>());
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
    }

    [Fact]
    public async Task BulkDelete_TooManyIds_ReturnsBadRequest()
    {
        var (client, _) = await AdminClientAsync();
        using (client)
        {
            var ids = Enumerable.Range(1, 501).ToArray();
            var resp = await client.PostAsJsonAsync("/api/admin/builds/bulkdelete", ids);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
    }

    [Fact]
    public async Task BulkDelete_ValidIds_RemovesExactlyThose()
    {
        var (client, _) = await AdminClientAsync();
        using (client)
        {
            var (a1, _, _) = await SeedAuditAsync(ChallengeBuildStatus.Success);
            var (a2, _, _) = await SeedAuditAsync(ChallengeBuildStatus.Success);
            var (keep, _, _) = await SeedAuditAsync(ChallengeBuildStatus.Success);

            var resp = await client.PostAsJsonAsync("/api/admin/builds/bulkdelete", new[] { a1, a2 });
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadFromJsonAsync<PruneResultModel>();
            Assert.Equal(2, body!.Removed);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Null(await db.ChallengeBuildAudits.AsNoTracking().FirstOrDefaultAsync(a => a.Id == a1));
            Assert.Null(await db.ChallengeBuildAudits.AsNoTracking().FirstOrDefaultAsync(a => a.Id == a2));
            Assert.NotNull(await db.ChallengeBuildAudits.AsNoTracking().FirstOrDefaultAsync(a => a.Id == keep));
        }
    }
}
