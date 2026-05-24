using System.ComponentModel.DataAnnotations;
using System.Linq;
using GZCTF.Models.Data;
using GZCTF.Models.Transfer;
using GZCTF.Utils;
using Xunit;

namespace GZCTF.Test.UnitTests.Transfer;

/// <summary>
/// Tests for the Attack &amp; Defense extensions to the challenge.yml transfer
/// format added in Phase 1 slice 1H.
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
        AdTickSeconds = 90,
        AdFlagLifetimeTicks = 7,
        AdAllowEgress = true,
        AdAllowSelfReset = false,
        AdResetCooldownMinutes = 10,
        AdAllowSnapshotDownload = false,
        AdPutflagWindowFraction = 0.35,
        AdGetflagWindowFraction = 0.55,
        AdMinGracePeriodSeconds = 5
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
        Assert.Equal(90, ad.TickSeconds);
        Assert.Equal(7, ad.FlagLifetimeTicks);
        Assert.True(ad.AllowEgress);
        Assert.False(ad.AllowSelfReset);
        Assert.Equal(10, ad.ResetCooldownMinutes);
        Assert.False(ad.AllowSnapshotDownload);
        Assert.Equal(0.35, ad.PutflagWindowFraction);
        Assert.Equal(0.55, ad.GetflagWindowFraction);
        Assert.Equal(5, ad.MinGracePeriodSeconds);
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
        // Round-trip: GameChallenge → TransferChallenge → GameChallenge with
        // Ad fields preserved.
        var original = MakeAdChallenge();

        var roundTripped = original.ToTransfer().ToChallenge();

        Assert.Equal(ChallengeType.AttackDefense, roundTripped.Type);
        Assert.Equal(original.AdCheckerImage, roundTripped.AdCheckerImage);
        Assert.Equal(original.AdTickSeconds, roundTripped.AdTickSeconds);
        Assert.Equal(original.AdFlagLifetimeTicks, roundTripped.AdFlagLifetimeTicks);
        Assert.Equal(original.AdAllowEgress, roundTripped.AdAllowEgress);
        Assert.Equal(original.AdAllowSelfReset, roundTripped.AdAllowSelfReset);
        Assert.Equal(original.AdResetCooldownMinutes, roundTripped.AdResetCooldownMinutes);
        Assert.Equal(original.AdAllowSnapshotDownload, roundTripped.AdAllowSnapshotDownload);
        Assert.Equal(original.AdPutflagWindowFraction, roundTripped.AdPutflagWindowFraction);
        Assert.Equal(original.AdGetflagWindowFraction, roundTripped.AdGetflagWindowFraction);
        Assert.Equal(original.AdMinGracePeriodSeconds, roundTripped.AdMinGracePeriodSeconds);

        // Container fields round-trip too (A&D inherits the same container shape).
        Assert.Equal(original.ContainerImage, roundTripped.ContainerImage);
        Assert.Equal(original.MemoryLimit, roundTripped.MemoryLimit);
        Assert.Equal(original.CPUCount, roundTripped.CPUCount);
        Assert.Equal(original.ExposePort, roundTripped.ExposePort);
    }

    [Fact]
    public void ToChallenge_OmittedAdFields_KeepGameChallengeDefaults()
    {
        // When operator omits optional ad.* fields, the resulting GameChallenge
        // should retain its column defaults (120s tick, etc.) rather than getting
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
        // Tick + lifetime + windows retained from the GameChallenge default
        // initializer (120 / 5 / 0.4 / 0.5 / 3).
        Assert.Equal(120, c.AdTickSeconds);
        Assert.Equal(5, c.AdFlagLifetimeTicks);
        Assert.Equal(0.4, c.AdPutflagWindowFraction);
        Assert.Equal(0.5, c.AdGetflagWindowFraction);
        Assert.Equal(3, c.AdMinGracePeriodSeconds);
        // Booleans default per GameChallenge initializer:
        Assert.False(c.AdAllowEgress);
        Assert.True(c.AdAllowSelfReset);
        Assert.True(c.AdAllowSnapshotDownload);
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
