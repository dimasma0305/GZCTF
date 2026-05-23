using System.Net;
using System.Net.Http.Json;
using GZCTF.Integration.Test.Base;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Models.Request.Account;
using GZCTF.Models.Request.Edit;
using GZCTF.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace GZCTF.Integration.Test.Tests.Api;

/// <summary>
/// Coverage for <c>/api/admin/repobindings</c> CRUD endpoints. These
/// tests deliberately set <c>RunImmediately = false</c> so the create
/// path doesn't trigger a real git clone — the discovery flow has its
/// own dedicated suite (<see cref="RepoBindingDiscoveryTests"/>).
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class RepoBindingControllerTests(GZCTFApplicationFactory factory, ITestOutputHelper output)
{
    private const string AdminPassword = "Admin@Binding123";

    /// <summary>Create a fresh admin and return a logged-in HttpClient.</summary>
    private async Task<HttpClient> AdminClientAsync()
    {
        var admin = await TestDataSeeder.CreateUserAsync(factory.Services,
            TestDataSeeder.RandomName(), AdminPassword, role: Role.Admin);
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/Account/LogIn",
            new LoginModel { UserName = admin.UserName, Password = AdminPassword });
        login.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>
    /// Returns a unique-per-test repo URL so the
    /// <c>IX_GameRepoBindings_RepoUrl</c> unique constraint doesn't bite
    /// when the test run repeats inside one process (xUnit collection).
    /// </summary>
    private static string UniqueRepoUrl(string scope) =>
        $"https://github.com/test-{scope}/{TestDataSeeder.RandomName()}";

    [Fact]
    public async Task CreateBinding_HappyPath_ReturnsOkAndPersists()
    {
        using var client = await AdminClientAsync();
        var url = UniqueRepoUrl("create");
        var resp = await client.PostAsJsonAsync("/api/admin/repobindings", new RepoBindingCreateModel
        {
            RepoUrl = url,
            IntervalSeconds = 120,
            RunImmediately = false,
        });

        resp.EnsureSuccessStatusCode();

        // Persisted row exists with the right defaults.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.GameRepoBindings.AsNoTracking().FirstOrDefaultAsync(b => b.RepoUrl == url);
        Assert.NotNull(row);
        Assert.Equal(120, row.IntervalSeconds);
        Assert.Equal(RepoWatchStatus.Active, row.Status);
        Assert.False(row.PushOnEdit);
        Assert.Null(row.GitHubTokenEncrypted);
        Assert.Equal(TokenStatus.NotConfigured, row.TokenStatus);
    }

    [Fact]
    public async Task CreateBinding_InvalidUrl_ReturnsBadRequest()
    {
        using var client = await AdminClientAsync();
        var resp = await client.PostAsJsonAsync("/api/admin/repobindings", new RepoBindingCreateModel
        {
            RepoUrl = "not-a-github-url",
            RunImmediately = false,
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task CreateBinding_DuplicateUrl_ReturnsConflict()
    {
        using var client = await AdminClientAsync();
        var url = UniqueRepoUrl("dup");
        var first = await client.PostAsJsonAsync("/api/admin/repobindings", new RepoBindingCreateModel
        {
            RepoUrl = url, RunImmediately = false,
        });
        first.EnsureSuccessStatusCode();

        var dup = await client.PostAsJsonAsync("/api/admin/repobindings", new RepoBindingCreateModel
        {
            RepoUrl = url, RunImmediately = false,
        });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task CreateBinding_IntervalBelowClamp_StoredAtMinimum()
    {
        using var client = await AdminClientAsync();
        var url = UniqueRepoUrl("clamp");
        var resp = await client.PostAsJsonAsync("/api/admin/repobindings", new RepoBindingCreateModel
        {
            RepoUrl = url,
            IntervalSeconds = 5,    // below 60 → server clamps
            RunImmediately = false,
        });
        resp.EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.GameRepoBindings.AsNoTracking().FirstAsync(b => b.RepoUrl == url);
        Assert.Equal(60, row.IntervalSeconds);
    }

    [Fact]
    public async Task ListBindings_ReturnsCreatedBinding_WithCorrectFlags()
    {
        using var client = await AdminClientAsync();
        var url = UniqueRepoUrl("list");
        var create = await client.PostAsJsonAsync("/api/admin/repobindings", new RepoBindingCreateModel
        {
            RepoUrl = url, RunImmediately = false, GitHubToken = "ghp_dummy",
        });
        create.EnsureSuccessStatusCode();

        var list = await client.GetFromJsonAsync<RepoBindingInfoModel[]>("/api/admin/repobindings");
        Assert.NotNull(list);
        var ours = list!.FirstOrDefault(b => b.RepoUrl == url);
        Assert.NotNull(ours);
        Assert.True(ours.HasGitHubToken);          // surface flag set when token present
        Assert.False(ours.PushOnEdit);             // default
    }

    [Fact]
    public async Task UpdateBinding_EachFieldIndependently()
    {
        using var client = await AdminClientAsync();
        var url = UniqueRepoUrl("update");
        var create = await client.PostAsJsonAsync("/api/admin/repobindings", new RepoBindingCreateModel
        {
            RepoUrl = url, RunImmediately = false, IntervalSeconds = 60,
        });
        create.EnsureSuccessStatusCode();

        int bindingId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            bindingId = (await db.GameRepoBindings.AsNoTracking().FirstAsync(b => b.RepoUrl == url)).Id;
        }

        // Bump interval + flip push-on-edit + add a token.
        var upd = await client.PutAsJsonAsync($"/api/admin/repobindings/{bindingId}", new RepoBindingUpdateModel
        {
            IntervalSeconds = 300,
            PushOnEdit = true,
            GitHubToken = "ghp_after_update",
        });
        upd.EnsureSuccessStatusCode();

        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db2.GameRepoBindings.AsNoTracking().FirstAsync(b => b.Id == bindingId);
        Assert.Equal(300, row.IntervalSeconds);
        Assert.True(row.PushOnEdit);
        Assert.NotNull(row.GitHubTokenEncrypted);
    }

    [Fact]
    public async Task UpdateBinding_GitHubTokenEmpty_ClearsStoredToken()
    {
        using var client = await AdminClientAsync();
        var url = UniqueRepoUrl("clear-tok");
        await (await client.PostAsJsonAsync("/api/admin/repobindings", new RepoBindingCreateModel
        {
            RepoUrl = url, RunImmediately = false, GitHubToken = "ghp_initial",
        })).EnsureSuccessStatusCodeAsync();

        int bindingId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            bindingId = (await db.GameRepoBindings.AsNoTracking().FirstAsync(b => b.RepoUrl == url)).Id;
        }

        var clear = await client.PutAsJsonAsync($"/api/admin/repobindings/{bindingId}", new RepoBindingUpdateModel
        {
            GitHubToken = "",   // "" = clear per documented convention
        });
        clear.EnsureSuccessStatusCode();

        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db2.GameRepoBindings.AsNoTracking().FirstAsync(b => b.Id == bindingId);
        Assert.Null(row.GitHubTokenEncrypted);
        Assert.Equal(TokenStatus.NotConfigured, row.TokenStatus);
    }

    [Fact]
    public async Task DeleteBinding_DefaultCascadeFalse_DetachesChildGames()
    {
        using var client = await AdminClientAsync();
        var url = UniqueRepoUrl("detach");

        // Create binding + seed a child game directly bound to it.
        await (await client.PostAsJsonAsync("/api/admin/repobindings", new RepoBindingCreateModel
        {
            RepoUrl = url, RunImmediately = false,
        })).EnsureSuccessStatusCodeAsync();

        int bindingId;
        int gameId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            bindingId = (await db.GameRepoBindings.AsNoTracking().FirstAsync(b => b.RepoUrl == url)).Id;
            var game = await TestDataSeeder.CreateGameAsync(factory.Services, $"DetachChild {TestDataSeeder.RandomName()}");
            gameId = game.Id;
            // Bind the game to our binding manually (would normally be the scanner's job).
            await db.Games.Where(g => g.Id == gameId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(g => g.RepoBindingId, bindingId)
                    .SetProperty(g => g.EventManifestPath, "fake/.gzevent"));
        }

        var del = await client.DeleteAsync($"/api/admin/repobindings/{bindingId}");
        del.EnsureSuccessStatusCode();

        // Game survives, binding link nulled.
        using var s = factory.Services.CreateScope();
        var d = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var stillThere = await d.Games.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameId);
        Assert.NotNull(stillThere);
        Assert.Null(stillThere.RepoBindingId);
        Assert.Null(stillThere.EventManifestPath);
    }

    [Fact]
    public async Task DeleteBinding_CascadeTrue_RemovesChildGames()
    {
        using var client = await AdminClientAsync();
        var url = UniqueRepoUrl("cascade");

        await (await client.PostAsJsonAsync("/api/admin/repobindings", new RepoBindingCreateModel
        {
            RepoUrl = url, RunImmediately = false,
        })).EnsureSuccessStatusCodeAsync();

        int bindingId;
        int gameId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            bindingId = (await db.GameRepoBindings.AsNoTracking().FirstAsync(b => b.RepoUrl == url)).Id;
            var game = await TestDataSeeder.CreateGameAsync(factory.Services, $"Cascade {TestDataSeeder.RandomName()}");
            gameId = game.Id;
            await db.Games.Where(g => g.Id == gameId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(g => g.RepoBindingId, bindingId)
                    .SetProperty(g => g.EventManifestPath, "fake/.gzevent"));
        }

        var del = await client.DeleteAsync($"/api/admin/repobindings/{bindingId}?cascade=true");
        del.EnsureSuccessStatusCode();

        using var s = factory.Services.CreateScope();
        var d = s.ServiceProvider.GetRequiredService<AppDbContext>();
        var gone = await d.Games.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameId);
        Assert.Null(gone);
    }
}

/// <summary>Tiny xUnit-friendly extension to chain EnsureSuccessStatusCode in async.</summary>
file static class HttpExtensions
{
    public static Task EnsureSuccessStatusCodeAsync(this HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        return Task.CompletedTask;
    }
}
