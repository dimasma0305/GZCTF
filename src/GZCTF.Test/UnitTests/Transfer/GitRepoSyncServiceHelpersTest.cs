using System;
using System.Text;
using GZCTF.Services.Transfer;
using Xunit;

namespace GZCTF.Test.UnitTests.Transfer;

/// <summary>
/// Static-helper coverage for <see cref="GitRepoSyncService"/>. These
/// were the source of the production "could not read Username" bug
/// before the switch to HTTP Basic — having explicit assertions keeps
/// future contributors from "improving" the format back to Bearer.
/// </summary>
public class GitRepoSyncServiceHelpersTest
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildAuthArgs_NoToken_ReturnsEmpty(string? token)
    {
        Assert.Empty(GitRepoSyncService.BuildAuthArgs(token));
    }

    [Fact]
    public void BuildAuthArgs_ClassicPat_UsesBasicAuthWithXAccessToken()
    {
        var pat = "ghp_testtoken123";
        var args = GitRepoSyncService.BuildAuthArgs(pat);

        Assert.Equal(2, args.Length);
        Assert.Equal("-c", args[0]);

        var expected = "Authorization: Basic "
            + Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{pat}"));
        Assert.Equal($"http.extraHeader={expected}", args[1]);
    }

    [Fact]
    public void BuildAuthArgs_FineGrainedPat_UsesSameBasicFormat()
    {
        // Fine-grained PATs (github_pat_*) take the exact same auth
        // path as classic ghp_* tokens — github's smart-HTTP doesn't
        // care about the prefix.
        var pat = "github_pat_11AABBCC_secret";
        var args = GitRepoSyncService.BuildAuthArgs(pat);

        var expected = "Authorization: Basic "
            + Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{pat}"));
        Assert.Equal($"http.extraHeader={expected}", args[1]);
    }

    [Fact]
    public void BuildAuthArgs_NeverEmitsBearerScheme()
    {
        // Regression guard: a "Bearer <pat>" header would compile but
        // git would fail with "could not read Username for github.com".
        var args = GitRepoSyncService.BuildAuthArgs("ghp_xxx");
        Assert.DoesNotContain(args, a => a.Contains("Bearer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SafeCommandSummary_StripsAuthHeaderBeforeFirstC()
    {
        // The args used in production look like:
        // ["-c", "http.extraHeader=Authorization: Basic SECRET", "push", "origin", "HEAD:main"]
        // SafeCommandSummary should yield "push origin HEAD:main" — the
        // PAT lives between the first `-c` and the next non-`-c` arg.
        string[] args = ["-c", "http.extraHeader=Authorization: Basic SECRETPAT", "push", "origin", "HEAD:main"];

        var summary = GitRepoSyncService.SafeCommandSummary(args);

        Assert.DoesNotContain("SECRETPAT", summary);
        Assert.DoesNotContain("Authorization", summary);
    }

    [Fact]
    public void SafeCommandSummary_NoAuthArgs_ReturnsFullCommand()
    {
        // No -c → no secrets to scrub; return full command for debug.
        string[] args = ["status", "--porcelain"];

        var summary = GitRepoSyncService.SafeCommandSummary(args);

        Assert.Contains("status", summary);
        Assert.Contains("--porcelain", summary);
    }
}
