using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
/// End-to-end coverage of the .tar.gz challenge import pipeline. The
/// real bug this guards against: prior to commit 4ff357bd, the yaml
/// parser used snake_case aliases that silently dropped the entire
/// <c>container:</c> block because the upstream schema uses camelCase.
/// Re-introducing snake_case here would make every container-typed
/// import lose its image / port config — exactly the kind of silent
/// corruption a test should catch.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public class ChallengeImportYamlTests(GZCTFApplicationFactory factory, ITestOutputHelper output)
{
    private const string AdminPassword = "Admin@Import123";

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
    /// Build a single-challenge .tar.gz in memory. Each entry path is
    /// relative to the tar root; the extractor preserves the structure
    /// verbatim into the temp workdir.
    /// </summary>
    private static byte[] BuildTarball(IDictionary<string, string> filesByPath)
    {
        using var msTarGz = new MemoryStream();
        using (var gz = new GZipStream(msTarGz, CompressionMode.Compress, leaveOpen: true))
        using (var tar = new TarWriter(gz, leaveOpen: false))
        {
            foreach (var (path, content) in filesByPath)
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = new MemoryStream(bytes),
                };
                tar.WriteEntry(entry);
            }
        }
        return msTarGz.ToArray();
    }

    private static async Task<HttpResponseMessage> PostTarballAsync(
        HttpClient client, int gameId, byte[] tarball, string filename = "test.tar.gz")
    {
        using var content = new MultipartFormDataContent();
        var stream = new ByteArrayContent(tarball);
        stream.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        content.Add(stream, "archive", filename);
        return await client.PostAsync($"/api/Edit/Games/{gameId}/Challenges/Import", content);
    }

    [Fact]
    public async Task Import_CamelCaseContainerImage_PersistsCorrectly()
    {
        // Regression guard for the snake_case bug. The yaml uses
        // `containerImage:` per the upstream gzcli schema; the parser
        // must read it back into ContainerImage on the DB row.
        using var client = await AdminClientAsync();
        var game = await TestDataSeeder.CreateGameAsync(factory.Services, $"ImportTest {TestDataSeeder.RandomName()}");
        var slug = TestDataSeeder.RandomName();
        var yaml = $$"""
            name: "{{slug}}"
            type: "StaticContainer"
            category: "Web"
            description: |
              Sample container challenge.
            flags:
              - "flag{test}"
            container:
              containerImage: "registry.example.com/app:1"
              memoryLimit: 256
              cpuCount: 2
              exposePort: 1337
            """;
        var tar = BuildTarball(new Dictionary<string, string>
        {
            ["challenge.yml"] = yaml,
        });

        var resp = await PostTarballAsync(client, game.Id, tar);
        resp.EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ch = await db.GameChallenges.AsNoTracking()
            .FirstOrDefaultAsync(c => c.GameId == game.Id && c.Title == slug);
        Assert.NotNull(ch);
        Assert.Equal("registry.example.com/app:1", ch.ContainerImage);
        Assert.Equal(256, ch.MemoryLimit);
        Assert.Equal(2, ch.CPUCount);
        Assert.Equal(1337, ch.ExposePort);
        // Registry-image-only path: ResolveBuildIntent returns
        // NotApplicable for any image string that isn't a Dockerfile
        // path or a gzctf-auto/ deterministic tag.
        Assert.Equal(ChallengeBuildStatus.NotApplicable, ch.BuildStatus);
    }

    [Fact]
    public async Task Import_RegistryImageWithoutDockerfile_KeepsImageNoBuild()
    {
        using var client = await AdminClientAsync();
        var game = await TestDataSeeder.CreateGameAsync(factory.Services, $"NoBuild {TestDataSeeder.RandomName()}");
        var slug = TestDataSeeder.RandomName();
        var yaml = $$"""
            name: "{{slug}}"
            type: "StaticContainer"
            category: "Pwn"
            flags: ["flag{nobuild}"]
            container:
              containerImage: "ghcr.io/already-published/img:1.2.3"
              exposePort: 9000
            """;
        var tar = BuildTarball(new Dictionary<string, string>
        {
            ["challenge.yml"] = yaml,
        });

        var resp = await PostTarballAsync(client, game.Id, tar);
        resp.EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ch = await db.GameChallenges.AsNoTracking()
            .FirstOrDefaultAsync(c => c.GameId == game.Id && c.Title == slug);
        Assert.NotNull(ch);
        Assert.Equal("ghcr.io/already-published/img:1.2.3", ch.ContainerImage);
        // Registry-image-only path → NotApplicable (no Dockerfile to build).
        Assert.Equal(ChallengeBuildStatus.NotApplicable, ch.BuildStatus);
    }

    [Fact]
    public async Task Import_StaticAttachment_BuildStatusNone()
    {
        using var client = await AdminClientAsync();
        var game = await TestDataSeeder.CreateGameAsync(factory.Services, $"Attach {TestDataSeeder.RandomName()}");
        var slug = TestDataSeeder.RandomName();
        var yaml = $$"""
            name: "{{slug}}"
            type: "StaticAttachment"
            category: "Misc"
            description: "An attachment-only challenge"
            flags: ["flag{attach}"]
            """;
        var tar = BuildTarball(new Dictionary<string, string>
        {
            ["challenge.yml"] = yaml,
        });

        var resp = await PostTarballAsync(client, game.Id, tar);
        resp.EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ch = await db.GameChallenges.AsNoTracking()
            .FirstOrDefaultAsync(c => c.GameId == game.Id && c.Title == slug);
        Assert.NotNull(ch);
        Assert.Equal(ChallengeType.StaticAttachment, ch.Type);
        Assert.Equal(ChallengeBuildStatus.None, ch.BuildStatus);
    }

    [Fact]
    public async Task Import_PopulatesSourceYamlPath_RelativeToWorkDir()
    {
        // SourceYamlPath enables the push-back feature; it must reflect
        // the yaml's path within the tarball root, NOT an absolute path
        // (which would leak the server's temp dir).
        using var client = await AdminClientAsync();
        var game = await TestDataSeeder.CreateGameAsync(factory.Services, $"YamlPath {TestDataSeeder.RandomName()}");
        var slug = TestDataSeeder.RandomName();
        var yaml = $$"""
            name: "{{slug}}"
            type: "StaticAttachment"
            category: "Misc"
            flags: ["flag{path}"]
            """;
        var tar = BuildTarball(new Dictionary<string, string>
        {
            // Nested path inside the tarball root.
            ["events/web/challenge.yml"] = yaml,
        });

        var resp = await PostTarballAsync(client, game.Id, tar);
        resp.EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ch = await db.GameChallenges.AsNoTracking()
            .FirstOrDefaultAsync(c => c.GameId == game.Id && c.Title == slug);
        Assert.NotNull(ch);
        Assert.NotNull(ch.SourceYamlPath);
        // Path is relative; must not start with /tmp or anything absolute.
        Assert.False(Path.IsPathRooted(ch.SourceYamlPath));
        Assert.EndsWith("challenge.yml", ch.SourceYamlPath);
    }

    [Fact]
    public async Task ReImport_OverwritesYamlFields()
    {
        using var client = await AdminClientAsync();
        var game = await TestDataSeeder.CreateGameAsync(factory.Services, $"ReImport {TestDataSeeder.RandomName()}");
        var slug = TestDataSeeder.RandomName();

        var v1 = $$"""
            name: "{{slug}}"
            type: "StaticAttachment"
            category: "Web"
            description: "First version description"
            flags: ["flag{v1}"]
            """;
        await (await PostTarballAsync(client, game.Id, BuildTarball(new Dictionary<string, string>
        {
            ["challenge.yml"] = v1,
        }))).EnsureSuccessStatusCodeAsync();

        var v2 = $$"""
            name: "{{slug}}"
            type: "StaticAttachment"
            category: "Pwn"
            description: "Second version description"
            flags: ["flag{v1}", "flag{v2}"]
            """;
        await (await PostTarballAsync(client, game.Id, BuildTarball(new Dictionary<string, string>
        {
            ["challenge.yml"] = v2,
        }))).EnsureSuccessStatusCodeAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ch = await db.GameChallenges.AsNoTracking()
            .Include(c => c.Flags)
            .FirstAsync(c => c.GameId == game.Id && c.Title == slug);
        // Description + category updated to v2's values.
        Assert.Contains("Second version description", ch.Content);
        Assert.Equal(ChallengeCategory.Pwn, ch.Category);
        // Existing flag preserved + new one added (no removal on re-import).
        var flagTexts = ch.Flags.Select(f => f.Flag).ToHashSet();
        Assert.Contains("flag{v1}", flagTexts);
        Assert.Contains("flag{v2}", flagTexts);
    }

    [Fact]
    public async Task Import_AuthorPrefixedIntoContent()
    {
        // The importer's contract is to render `author: X` from yaml as
        // a Markdown prefix into Content: "Author: **X**\n\n...". The
        // round-trip serializer (covered in unit tests) then strips this
        // back out on push-back.
        using var client = await AdminClientAsync();
        var game = await TestDataSeeder.CreateGameAsync(factory.Services, $"Author {TestDataSeeder.RandomName()}");
        var slug = TestDataSeeder.RandomName();
        var yaml = $$"""
            name: "{{slug}}"
            author: "alice"
            type: "StaticAttachment"
            category: "Misc"
            description: "Body text only."
            flags: ["flag{auth}"]
            """;
        await (await PostTarballAsync(client, game.Id, BuildTarball(new Dictionary<string, string>
        {
            ["challenge.yml"] = yaml,
        }))).EnsureSuccessStatusCodeAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ch = await db.GameChallenges.AsNoTracking()
            .FirstAsync(c => c.GameId == game.Id && c.Title == slug);
        Assert.StartsWith("Author: **alice**", ch.Content);
        Assert.Contains("Body text only.", ch.Content);
    }
}

file static class HttpExtensions2
{
    public static Task EnsureSuccessStatusCodeAsync(this HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        return Task.CompletedTask;
    }
}
