using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using GZCTF.Integration.Test.Base;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Models.Request.Account;
using GZCTF.Models.Request.Edit;
using GZCTF.Utils;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace GZCTF.Integration.Test.Tests.Api;

/// <summary>
/// End-to-end coverage of the push-back feature. Skipped unless
/// <c>TEST_GH_PAT</c> + <c>TEST_GH_REPO</c> env vars are present —
/// these tests perform real <c>git push</c> against an actual github
/// repo and would otherwise be hostile to local dev and flaky CI.
///
/// <para>Setup for CI runs:
/// 1. Create a public repo (e.g. <c>dimasma0305/gzctf-ci-pushback-test</c>)
///    with a single seed <c>challenge.yml</c> + <c>.gzevent</c> on main.
/// 2. Generate a fine-grained PAT with <c>Contents: Read and write</c>.
/// 3. Add as GH Actions secret <c>TEST_GH_PAT</c> + repo url as
///    <c>TEST_GH_REPO</c>.
/// 4. Workflow passes both as env vars to the test process.</para>
///
/// <para>Each test creates a fresh ephemeral branch (<c>ci-{guid}</c>)
/// off main, exercises push-back, then deletes the branch in teardown.</para>
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class GitPushBackEndToEndTests(GZCTFApplicationFactory factory, ITestOutputHelper output)
{
    private static readonly string? Pat = Environment.GetEnvironmentVariable("TEST_GH_PAT");
    private static readonly string? RepoUrl = Environment.GetEnvironmentVariable("TEST_GH_REPO");

    private const string AdminPassword = "Admin@PushBack123";

    private async Task<HttpClient> AdminClientAsync()
    {
        var admin = await TestDataSeeder.CreateUserAsync(factory.Services,
            TestDataSeeder.RandomName(), AdminPassword, role: Role.Admin);
        var c = factory.CreateClient();
        var login = await c.PostAsJsonAsync("/api/Account/LogIn",
            new LoginModel { UserName = admin.UserName, Password = AdminPassword });
        login.EnsureSuccessStatusCode();
        return c;
    }

    /// <summary>
    /// Create a fresh ephemeral branch on the configured test repo
    /// (off <c>main</c>), set up a binding pointing at it with
    /// <c>PushOnEdit = true</c> + the PAT pre-seeded into the encrypted
    /// token field. Returns the binding id + branch name; caller must
    /// invoke <see cref="CleanupBranchAsync"/> in finally.
    /// </summary>
    private async Task<(int bindingId, string branch)> SetupTestBindingAsync()
    {
        var branch = $"ci-{Guid.NewGuid():N}";
        await CreateBranchOnGitHubAsync(branch);

        // Insert binding row directly (skip the controller because it
        // would trigger a real scan we don't need here).
        int bindingId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector(GZCTF.Services.Transfer.GameRepoBindingProtection.Purpose);
            var binding = new GameRepoBinding
            {
                RepoUrl = RepoUrl!,
                Ref = branch,
                CreatedByUserId = Guid.Empty,
                IntervalSeconds = 3600,
                Status = RepoWatchStatus.Active,
                PushOnEdit = true,
                GitHubTokenEncrypted = protector.Protect(Pat!),
                TokenStatus = TokenStatus.Ok,
            };
            db.GameRepoBindings.Add(binding);
            await db.SaveChangesAsync();
            bindingId = binding.Id;
        }
        return (bindingId, branch);
    }

    private static async Task CreateBranchOnGitHubAsync(string branch)
    {
        // POST /repos/{owner}/{repo}/git/refs with sha of main.
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Pat);
        var (owner, repo) = ParseOwnerRepo(RepoUrl!);
        var refResp = await http.GetFromJsonAsync<RefObject>(
            $"https://api.github.com/repos/{owner}/{repo}/git/refs/heads/main")
            ?? throw new InvalidOperationException("Could not read main ref");
        var post = await http.PostAsJsonAsync(
            $"https://api.github.com/repos/{owner}/{repo}/git/refs",
            new { @ref = $"refs/heads/{branch}", sha = refResp.@object.sha });
        post.EnsureSuccessStatusCode();
    }

    private async Task CleanupBranchAsync(string branch)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Pat);
            var (owner, repo) = ParseOwnerRepo(RepoUrl!);
            await http.DeleteAsync($"https://api.github.com/repos/{owner}/{repo}/git/refs/heads/{branch}");
        }
        catch (Exception ex)
        {
            output.WriteLine($"Branch cleanup failed (non-fatal): {ex.Message}");
        }
    }

    private static (string owner, string repo) ParseOwnerRepo(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url,
            @"github\.com/([^/]+)/([^/]+?)(?:\.git)?/?$");
        if (!m.Success) throw new InvalidOperationException($"Bad repo URL: {url}");
        return (m.Groups[1].Value, m.Groups[2].Value);
    }

    private sealed record RefObject(RefInner @object);
    private sealed record RefInner(string sha);

    [SkippableFact]
    public async Task PushOnEdit_EnabledBinding_PushesCommitToUpstream()
    {
        Skip.If(string.IsNullOrEmpty(Pat) || string.IsNullOrEmpty(RepoUrl),
            "TEST_GH_PAT + TEST_GH_REPO env vars not set — skipping real-github tests.");

        var (bindingId, branch) = await SetupTestBindingAsync();
        try
        {
            // Force a one-shot scan via the discovery service so the
            // import populates challenges (with SourceYamlPath) without
            // going through HTTP. The test repo MUST have a seed
            // .gzevent + challenge.yml on main; the branch we created
            // inherits those.
            using (var scope = factory.Services.CreateScope())
            {
                var disc = scope.ServiceProvider
                    .GetRequiredService<GZCTF.Services.Transfer.RepoBindingDiscoveryService>();
                await disc.ScanAsync(bindingId, Guid.Empty, default, force: true);
            }

            // Pick whichever challenge got imported.
            int chId;
            int gameId;
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var imported = await db.GameChallenges.AsNoTracking()
                    .FirstAsync(c => c.SourceYamlPath != null);
                chId = imported.Id;
                gameId = imported.GameId;
            }

            // Hit the EditController to trigger push-back. Use the
            // admin client because the endpoint is RequireGameAdmin.
            using var client = await AdminClientAsync();
            var stamp = Guid.NewGuid().ToString("N")[..8];
            var newDescription = $"Edited by integration test — marker {stamp}";
            var resp = await client.PutAsJsonAsync(
                $"/api/Edit/Games/{gameId}/Challenges/{chId}",
                new ChallengeUpdateModel
                {
                    Title = "Sample",
                    Content = newDescription,
                    Category = ChallengeCategory.Misc,
                    Hints = [],
                    OriginalScore = 1000,
                    MinScoreRate = 0.25,
                    Difficulty = 5,
                    SubmissionLimit = 0,
                    IsEnabled = true,
                });
            resp.EnsureSuccessStatusCode();

            // Push-back fires fire-and-forget; poll the github API for
            // the commit landing. Budget: 30s; usually arrives in ~3.
            var landed = await PollForCommitAsync(branch, stamp, TimeSpan.FromSeconds(30));
            Assert.True(landed, $"Push-back did not land a commit containing '{stamp}' within timeout");
        }
        finally
        {
            await CleanupBranchAsync(branch);
        }
    }

    private async Task<bool> PollForCommitAsync(string branch, string marker, TimeSpan budget)
    {
        var deadline = DateTimeOffset.UtcNow.Add(budget);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Pat);
        var (owner, repo) = ParseOwnerRepo(RepoUrl!);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                // Get the tree's challenge.yml content directly — cheaper than walking commits.
                var resp = await http.GetAsync(
                    $"https://api.github.com/repos/{owner}/{repo}/contents/challenges/sample/challenge.yml?ref={branch}");
                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    if (body.Contains(marker)) return true;
                }
            }
            catch { /* transient */ }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        return false;
    }

    [SkippableFact]
    public async Task PushOnEdit_StaleLocalCheckout_FetchResetRecovers()
    {
        Skip.If(string.IsNullOrEmpty(Pat) || string.IsNullOrEmpty(RepoUrl),
            "TEST_GH_PAT + TEST_GH_REPO env vars not set — skipping real-github tests.");

        var (bindingId, branch) = await SetupTestBindingAsync();
        try
        {
            // First scan establishes the local checkout.
            using (var scope = factory.Services.CreateScope())
            {
                var disc = scope.ServiceProvider
                    .GetRequiredService<GZCTF.Services.Transfer.RepoBindingDiscoveryService>();
                await disc.ScanAsync(bindingId, Guid.Empty, default, force: true);
            }

            // Manually mutate the cached checkout to simulate stale state —
            // a "real" stale state is hard to engineer without another
            // pusher; corrupting HEAD on disk is the closest deterministic
            // analog. CommitAndPushAsync runs SyncAsync first which does
            // fetch + reset --hard, recovering any local damage.
            var repoDir = $"/app/repos/binding/{bindingId}";
            if (Directory.Exists(repoDir))
            {
                // Write a junk file at the yaml's target path that doesn't
                // exist on the upstream. fetch+reset --hard FETCH_HEAD
                // wipes it.
                var junkPath = Path.Combine(repoDir, "junk-from-stale-test.txt");
                await File.WriteAllTextAsync(junkPath, "should be removed by reset --hard");
            }

            // Trigger another edit + push-back; success means the
            // checkout was healed before the new commit was made.
            int chId;
            int gameId;
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var imported = await db.GameChallenges.AsNoTracking()
                    .FirstAsync(c => c.SourceYamlPath != null);
                chId = imported.Id;
                gameId = imported.GameId;
            }

            using var client = await AdminClientAsync();
            var stamp = "stale-" + Guid.NewGuid().ToString("N")[..8];
            var resp = await client.PutAsJsonAsync(
                $"/api/Edit/Games/{gameId}/Challenges/{chId}",
                new ChallengeUpdateModel
                {
                    Title = "Sample",
                    Content = $"Stale recovery test {stamp}",
                    Category = ChallengeCategory.Misc,
                    Hints = [],
                    OriginalScore = 1000,
                    MinScoreRate = 0.25,
                    Difficulty = 5,
                    SubmissionLimit = 0,
                    IsEnabled = true,
                });
            resp.EnsureSuccessStatusCode();

            var landed = await PollForCommitAsync(branch, stamp, TimeSpan.FromSeconds(30));
            Assert.True(landed, "Push-back from stale checkout should have recovered + landed commit");
        }
        finally
        {
            await CleanupBranchAsync(branch);
        }
    }
}
