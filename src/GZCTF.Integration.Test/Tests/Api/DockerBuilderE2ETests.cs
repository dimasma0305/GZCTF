using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using GZCTF.Integration.Test.Base;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Models.Request.Account;
using GZCTF.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace GZCTF.Integration.Test.Tests.Api;

/// <summary>
/// End-to-end coverage of the docker builder pipeline. Skipped unless
/// running in Local Mode (the existing testcontainers infrastructure
/// already requires a docker daemon there). Cloud Mode uses k3s + the
/// <see cref="GZCTF.Services.Container.Build.K8sChallengeImageBuilder"/>
/// which has its own (out-of-scope) coverage path.
///
/// <para>These tests actually run <c>docker build</c> against a tiny
/// alpine fixture, so each one is ~10–20s. Worth it: they're the only
/// thing that exercises the async queue, the JSONMessage progress sink,
/// the cleanup logic, and the audit row state machine together.</para>
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class DockerBuilderE2ETests(GZCTFApplicationFactory factory, ITestOutputHelper output)
{
    private const string AdminPassword = "Admin@Build123";
    private static readonly bool IsLocalMode =
        string.Equals(Environment.GetEnvironmentVariable("GZCTF_INTEGRATION_TEST_MODE"),
            "local", StringComparison.OrdinalIgnoreCase);

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
    /// Build a tarball with the minimum a buildable challenge needs:
    /// a yaml whose containerImage points at <c>./Dockerfile</c>, plus
    /// the Dockerfile itself.
    /// </summary>
    private static byte[] BuildableTarball(string slug)
    {
        var dockerfile = "FROM alpine:3.20\nRUN echo \"hello from " + slug + "\"\nCMD [\"sh\", \"-c\", \"sleep 60\"]\n";
        var yaml = $$"""
            name: "{{slug}}"
            type: "StaticContainer"
            category: "Misc"
            flags: ["flag{build}"]
            container:
              containerImage: "./Dockerfile"
              exposePort: 80
              memoryLimit: 64
              cpuCount: 1
            """;
        var files = new Dictionary<string, string>
        {
            ["challenge.yml"] = yaml,
            ["Dockerfile"] = dockerfile,
        };
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
        using (var tar = new TarWriter(gz, leaveOpen: false))
        {
            foreach (var (path, content) in files)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
                };
                tar.WriteEntry(entry);
            }
        }
        return ms.ToArray();
    }

    private static async Task<HttpResponseMessage> PostTarballAsync(
        HttpClient client, int gameId, byte[] bytes)
    {
        using var content = new MultipartFormDataContent();
        var stream = new ByteArrayContent(bytes);
        stream.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        content.Add(stream, "archive", "build-test.tar.gz");
        return await client.PostAsync($"/api/Edit/Games/{gameId}/Challenges/Import", content);
    }

    [SkippableFact]
    public async Task ImportAndBuild_EndToEnd_FlipsStatusToSuccess()
    {
        Skip.IfNot(IsLocalMode, "Docker builder tests only run in Local Mode (Cloud Mode = k3s).");

        using var client = await AdminClientAsync();
        var game = await TestDataSeeder.CreateGameAsync(factory.Services,
            $"BuildE2E {TestDataSeeder.RandomName()}");
        var slug = TestDataSeeder.RandomName().ToLowerInvariant();

        var resp = await PostTarballAsync(client, game.Id, BuildableTarball(slug));
        resp.EnsureSuccessStatusCode();

        // Wait for the async worker to pick up the job + complete the build.
        // Alpine pull + single-RUN build is ~5–15s on a warm daemon.
        var finalStatus = await PollChallengeStatusAsync(game.Id, slug, TimeSpan.FromMinutes(2));

        output.WriteLine($"final BuildStatus: {finalStatus}");
        Assert.Equal(ChallengeBuildStatus.Success, finalStatus);

        // Verify the deterministic tag lives on the daemon.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ch = await db.GameChallenges.AsNoTracking()
            .FirstAsync(c => c.GameId == game.Id && c.Title == slug);
        Assert.NotNull(ch.ContainerImage);
        Assert.StartsWith($"gzctf-auto/{game.Id}/", ch.ContainerImage);
        Assert.NotNull(ch.BuildImageDigest);
    }

    [SkippableFact]
    public async Task CleanupAfterBuild_DropsOlderSiblingTags()
    {
        Skip.IfNot(IsLocalMode, "Docker builder tests only run in Local Mode (Cloud Mode = k3s).");

        using var client = await AdminClientAsync();
        var game = await TestDataSeeder.CreateGameAsync(factory.Services,
            $"Cleanup {TestDataSeeder.RandomName()}");
        var slug = TestDataSeeder.RandomName().ToLowerInvariant();

        // Pre-seed a "stale" sibling tag directly on the daemon — this
        // simulates a previous edit's leftover. Use the same repository
        // namespace so the cleanup pass scoops it.
        var repository = $"gzctf-auto/{game.Id}/{slug}";
        var staleTag = $"{repository}:deadbeef0000";
        using (var http = new HttpClient(new SocketsHttpHandler())
        {
            BaseAddress = new Uri("http://localhost"),
        })
        using (var docker = new DockerClientConfiguration().CreateClient())
        {
            // Pull alpine first so we have a real image to tag.
            await docker.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = "alpine", Tag = "3.20" },
                authConfig: null,
                progress: new Progress<JSONMessage>());
            await docker.Images.TagImageAsync("alpine:3.20",
                new ImageTagParameters { RepositoryName = repository, Tag = "deadbeef0000" });
        }

        // Now import + build; the cleanup pass at end of build should drop the stale sibling.
        var resp = await PostTarballAsync(client, game.Id, BuildableTarball(slug));
        resp.EnsureSuccessStatusCode();
        var finalStatus = await PollChallengeStatusAsync(game.Id, slug, TimeSpan.FromMinutes(2));
        Assert.Equal(ChallengeBuildStatus.Success, finalStatus);

        // Verify the stale tag is gone.
        using (var docker = new DockerClientConfiguration().CreateClient())
        {
            var images = await docker.Images.ListImagesAsync(new ImagesListParameters { All = false });
            var staleStillThere = images.Any(i =>
                i.RepoTags is not null && i.RepoTags.Contains(staleTag));
            Assert.False(staleStillThere, $"Stale sibling tag {staleTag} should have been pruned");
        }
    }

    private async Task<ChallengeBuildStatus> PollChallengeStatusAsync(
        int gameId, string title, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        ChallengeBuildStatus last = ChallengeBuildStatus.None;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ch = await db.GameChallenges.AsNoTracking()
                .FirstOrDefaultAsync(c => c.GameId == gameId && c.Title == title);
            if (ch is not null)
            {
                last = ch.BuildStatus;
                if (last == ChallengeBuildStatus.Success ||
                    last == ChallengeBuildStatus.Failed ||
                    last == ChallengeBuildStatus.MissingDockerfile)
                {
                    return last;
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        output.WriteLine($"PollChallengeStatusAsync timed out; last seen: {last}");
        return last;
    }
}
