using System.Collections.Generic;
using System.Linq;
using GZCTF.Services;
using Xunit;

namespace GZCTF.Test.UnitTests.Services;

/// <summary>
/// Tests for the A&amp;D change-diff filter — the logic behind the AdOps "what did
/// the team change" view. Runtime/churn paths are hidden and <c>docker diff</c>'s
/// ancestor directories collapse to the changed leaf files.
/// </summary>
public class AdContainerManagerChangeFilterTests
{
    [Theory]
    [InlineData("/flag")]                                       // Docker flag bind-mount
    [InlineData("/gzctf-flag")]
    [InlineData("/gzctf-flag/flag")]                            // K8s flag sidecar mount
    [InlineData("/tmp/notes/chk123")]                           // checker probe file
    [InlineData("/run/x")]
    [InlineData("/proc/1/status")]
    [InlineData("/var/log/app.log")]
    [InlineData("/var/cache/apt/x")]
    [InlineData("/root/.cache/pip/y")]
    [InlineData("/app/__pycache__/app.cpython-312.pyc")]        // python bytecode
    public void IsNoiseChangePath_FiltersRuntimeChurn(string path)
        => Assert.True(AdContainerManager.IsNoiseChangePath(path));

    [Theory]
    [InlineData("/app/app.php")]   // a real team patch
    [InlineData("/serve.sh")]
    [InlineData("/app/app.py")]
    [InlineData("/etc/passwd")]
    public void IsNoiseChangePath_KeepsRealChanges(string path)
        => Assert.False(AdContainerManager.IsNoiseChangePath(path));

    [Fact]
    public void FilterAndCollapseChanges_CollapsesAncestorsAndDropsNoise()
    {
        // docker diff reports every ancestor dir of a change plus runtime churn;
        // only the real changed leaf file should survive.
        var entries = new List<(string Path, int Kind)>
        {
            ("/usr", 0),
            ("/usr/local", 0),
            ("/usr/local/lib/python3.12/__pycache__/x.pyc", 1),
            ("/app", 0),
            ("/app/app.php", 0),
            ("/flag", 1)
        };

        var result = AdContainerManager.FilterAndCollapseChanges(entries);

        Assert.Equal(new[] { "/app/app.php" }, result.Select(r => r.Path).ToArray());
    }

    [Fact]
    public void FilterAndCollapseChanges_KeepsSiblingLeaves()
    {
        var entries = new List<(string Path, int Kind)>
        {
            ("/app", 0),
            ("/app/a.txt", 0),
            ("/app/b.txt", 1)
        };

        var result = AdContainerManager.FilterAndCollapseChanges(entries)
            .Select(r => r.Path).OrderBy(p => p).ToArray();

        Assert.Equal(new[] { "/app/a.txt", "/app/b.txt" }, result);
    }

    [Fact]
    public void NoiseFilterCategories_AreSurfacedToOperators()
        => Assert.NotEmpty(AdContainerManager.NoiseFilterCategories);
}
