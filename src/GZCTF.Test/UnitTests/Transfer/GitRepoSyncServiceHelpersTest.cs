using System;
using System.IO;
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

    [Fact]
    public void SafeCommandSummary_KeepsOperationWhenAuthArgsPrependedFirst()
    {
        // Real shape: BuildAuthArgs prepends "-c <header>" at position 0, then the
        // operation. The summary must STILL show the operation. Regression: the
        // old TakeWhile-until-first-"-c" returned "" here → useless "git  exited".
        string[] args =
        [
            "-c", "http.extraHeader=Authorization: Basic SECRETPAT",
            "clone", "--depth", "1", "https://github.com/o/r.git", "1"
        ];

        var summary = GitRepoSyncService.SafeCommandSummary(args);

        Assert.DoesNotContain("SECRETPAT", summary);
        Assert.DoesNotContain("Authorization", summary);
        Assert.Contains("clone", summary);
        Assert.Contains("--depth", summary);
    }

    // --- stale-lock recovery (the binding-wedging bug) ---

    [Fact]
    public void ClearStaleGitLocks_RemovesTopLevelLocks_KeepsOtherFiles()
    {
        var gitDir = Path.Combine(Path.GetTempPath(), "gzctf-gitlock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(gitDir);
        try
        {
            File.WriteAllText(Path.Combine(gitDir, "shallow.lock"), "x");
            File.WriteAllText(Path.Combine(gitDir, "index.lock"), "x");
            File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main");

            var cleared = GitRepoSyncService.ClearStaleGitLocks(gitDir);

            Assert.Equal(2, cleared);
            Assert.False(File.Exists(Path.Combine(gitDir, "shallow.lock")));
            Assert.False(File.Exists(Path.Combine(gitDir, "index.lock")));
            Assert.True(File.Exists(Path.Combine(gitDir, "HEAD"))); // non-lock untouched
        }
        finally
        {
            Directory.Delete(gitDir, recursive: true);
        }
    }

    [Fact]
    public void ClearStaleGitLocks_MissingDir_ReturnsZero()
    {
        var missing = Path.Combine(Path.GetTempPath(), "gzctf-nope-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(0, GitRepoSyncService.ClearStaleGitLocks(missing));
    }

    // --- GitCommandException classification (drives concise-warning vs error log) ---

    [Theory]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled")]
    [InlineData("remote: Authentication failed for 'https://github.com/o/r'")]
    [InlineData("fatal: Invalid username or password")]
    public void GitCommandException_AuthFailures_FlaggedAsAuth(string stderr)
    {
        var ex = new GitCommandException("git fetch exited 128: " + stderr, 128, stderr);

        Assert.True(ex.IsAuthFailure);
        Assert.False(ex.IsStaleLock);
        Assert.Contains("authentication failed", ex.FriendlyMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GitCommandException_StaleLock_FlaggedAndFriendly()
    {
        var stderr = "fatal: Unable to create '/app/repos/binding/1/.git/shallow.lock': File exists.";
        var ex = new GitCommandException("git fetch exited 128: " + stderr, 128, stderr);

        Assert.True(ex.IsStaleLock);
        Assert.False(ex.IsAuthFailure);
        Assert.Contains("git lock", ex.FriendlyMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GitCommandException_DerivesFromInvalidOperation_SoExistingCatchesStillWork()
    {
        var ex = new GitCommandException("boom", 1, "boom");

        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Equal(1, ex.ExitCode);
    }
}
