using System.ComponentModel.DataAnnotations;
using System.Linq;
using GZCTF.Models.Data;
using GZCTF.Models.Transfer;
using GZCTF.Utils;
using Xunit;

namespace GZCTF.Test.UnitTests.Transfer;

/// <summary>
/// Tests for the Attack &amp; Defense extensions to the challenge.yml transfer
/// format. Per-challenge A&amp;D config is limited to the checker image + the two
/// network-policy toggles; tick length, flag lifetime, reset cooldown, snapshot
/// download, and the checker timing knobs (getflag jitter window + min grace
/// period) are EVENT-WIDE and live on <see cref="Game"/>, not the challenge — so
/// they are not part of the per-challenge transfer.
/// </summary>
public class TransferChallengeAdTests
{
    private static GameChallenge MakeAdChallenge() => new()
    {
        Id = 42,
        Title = "vuln-flask-app",
        Content = "Exploit the flask service",
        Category = ChallengeCategory.Web,
        Type = ChallengeType.AttackDefense,
        IsEnabled = true,
        OriginalScore = 0,
        MinScoreRate = 0,
        Difficulty = 0,
        SubmissionLimit = 0,
        ContainerImage = "ghcr.io/myorg/vuln-flask:1.0",
        MemoryLimit = 256,
        CPUCount = 2,
        StorageLimit = 512,
        ExposePort = 8080,
        NetworkMode = NetworkMode.Open,
        AdCheckerImage = "ghcr.io/myorg/vuln-flask-checker:1.0",
        AdAllowEgress = true,
        AdAllowSelfReset = false
    };

    [Fact]
    public void ToTransfer_AdChallenge_PopulatesAdSection()
    {
        var challenge = MakeAdChallenge();

        var transfer = challenge.ToTransfer();

        Assert.Equal(ChallengeType.AttackDefense, transfer.Type);
        Assert.NotNull(transfer.Container);
        Assert.Equal("ghcr.io/myorg/vuln-flask:1.0", transfer.Container!.Image);

        Assert.NotNull(transfer.Ad);
        var ad = transfer.Ad!;
        Assert.Equal("ghcr.io/myorg/vuln-flask-checker:1.0", ad.CheckerImage);
        Assert.True(ad.AllowEgress);
        Assert.False(ad.AllowSelfReset);
    }

    [Fact]
    public void ToTransfer_NonAdChallenge_AdSectionStaysNull()
    {
        var challenge = new GameChallenge
        {
            Title = "static-attach",
            Category = ChallengeCategory.Misc,
            Type = ChallengeType.StaticAttachment
        };

        var transfer = challenge.ToTransfer();

        Assert.Null(transfer.Ad);
    }

    [Fact]
    public void ToChallenge_AdSection_RoundTrips()
    {
        // Round-trip: GameChallenge → TransferChallenge → GameChallenge with the
        // per-challenge Ad fields preserved.
        var original = MakeAdChallenge();

        var roundTripped = original.ToTransfer().ToChallenge();

        Assert.Equal(ChallengeType.AttackDefense, roundTripped.Type);
        Assert.Equal(original.AdCheckerImage, roundTripped.AdCheckerImage);
        Assert.Equal(original.AdAllowEgress, roundTripped.AdAllowEgress);
        Assert.Equal(original.AdAllowSelfReset, roundTripped.AdAllowSelfReset);

        // Container fields round-trip too (A&D inherits the same container shape).
        Assert.Equal(original.ContainerImage, roundTripped.ContainerImage);
        Assert.Equal(original.MemoryLimit, roundTripped.MemoryLimit);
        Assert.Equal(original.CPUCount, roundTripped.CPUCount);
        Assert.Equal(original.ExposePort, roundTripped.ExposePort);
    }

    [Fact]
    public void ToChallenge_OmittedAdFields_KeepGameChallengeDefaults()
    {
        // When the operator omits the optional ad.* toggles, the resulting
        // GameChallenge should retain its column defaults rather than getting
        // null'd out.
        var transfer = new TransferChallenge
        {
            Title = "minimal",
            Category = ChallengeCategory.Web,
            Type = ChallengeType.AttackDefense,
            Container = new ContainerSection { Image = "img:1" },
            Ad = new AdSection { CheckerImage = "checker:1" }
        };

        var c = transfer.ToChallenge();

        Assert.Equal("checker:1", c.AdCheckerImage);
        // Booleans default per the GameChallenge initializer: egress is OPEN by
        // default (challenges reach the internet unless sandboxed), self-reset on.
        Assert.True(c.AdAllowEgress);
        Assert.True(c.AdAllowSelfReset);
    }

    [Fact]
    public void Validate_AdChallenge_MissingAdSection_YieldsError()
    {
        var transfer = new TransferChallenge
        {
            Title = "broken",
            Category = ChallengeCategory.Web,
            Type = ChallengeType.AttackDefense,
            Container = new ContainerSection { Image = "img:1" },
            Ad = null
        };

        var results = transfer.Validate(new ValidationContext(transfer)).ToList();

        Assert.Contains(results, r =>
            r.MemberNames.Contains(nameof(TransferChallenge.Ad))
            && r.ErrorMessage!.Contains("ad configuration"));
    }

    [Fact]
    public void Validate_AdChallenge_MissingContainerSection_YieldsError()
    {
        var transfer = new TransferChallenge
        {
            Title = "broken",
            Category = ChallengeCategory.Web,
            Type = ChallengeType.AttackDefense,
            Container = null,
            Ad = new AdSection { CheckerImage = "checker:1" }
        };

        var results = transfer.Validate(new ValidationContext(transfer)).ToList();

        Assert.Contains(results, r =>
            r.MemberNames.Contains(nameof(TransferChallenge.Container))
            && r.ErrorMessage!.Contains("container configuration"));
    }

    [Fact]
    public void Validate_AdChallenge_WithBothSections_NoErrors()
    {
        var transfer = new TransferChallenge
        {
            Title = "valid",
            Category = ChallengeCategory.Web,
            Type = ChallengeType.AttackDefense,
            Container = new ContainerSection { Image = "img:1" },
            Ad = new AdSection { CheckerImage = "checker:1" }
        };

        var results = transfer.Validate(new ValidationContext(transfer)).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Validate_DynamicContainer_Unaffected_By_AdValidation()
    {
        // Regression: introducing A&D validation didn't break the existing
        // DynamicContainer path (requires Container, doesn't care about Ad).
        var transfer = new TransferChallenge
        {
            Title = "regular dynamic container",
            Category = ChallengeCategory.Pwn,
            Type = ChallengeType.DynamicContainer,
            Container = new ContainerSection { Image = "img:1" },
            Ad = null
        };

        var results = transfer.Validate(new ValidationContext(transfer)).ToList();

        Assert.Empty(results);
    }
}
