using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GZCTF.Models.Data;
using GZCTF.Models.Request.Edit;
using GZCTF.Services.Transfer;
using GZCTF.Utils;
using Xunit;

namespace GZCTF.Test.UnitTests.Transfer;

/// <summary>
/// Regression tests locking in King-of-the-Hill parity with Attack &amp; Defense
/// across the challenge.yml import/export paths. KotH UsesAdEngine() but is NOT
/// IsAttackDefense(); several gates historically keyed on IsAttackDefense() and so
/// silently mistreated KotH — dropped its <c>ad:</c> block on import (giving the
/// shared hill public egress despite <c>allowEgress: false</c>), rejected a KotH
/// package for declaring no flag, and dropped the <c>ad:</c> block on push-back
/// export. Each of those gates must use UsesAdEngine() so both engine types behave
/// identically. (The whole-game JSON transfer path is deliberately NOT covered —
/// its AdSection.CheckerImage is [Required], which KotH cannot satisfy; KotH ad:
/// knobs round-trip through challenge.yml instead.)
/// </summary>
public sealed class KothImportParityTests : IDisposable
{
    readonly List<string> _tempDirs = new();

    string MakePackage(bool withSolver = true, bool withDockerfile = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gzctf-koth-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        if (withSolver)
        {
            Directory.CreateDirectory(Path.Combine(dir, "solver"));
            File.WriteAllText(Path.Combine(dir, "solver", "solve.py"), "print('exploit')\n");
        }
        if (withDockerfile)
        {
            Directory.CreateDirectory(Path.Combine(dir, "src"));
            File.WriteAllText(Path.Combine(dir, "src", "Dockerfile"), "FROM python:3.12-alpine\n");
        }
        return dir;
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs)
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
    }

    // ---- ValidateSubmissionShape: the flag-source exemption (ChallengeImportService.cs:419) ----

    [Fact]
    public void ValidateSubmissionShape_Koth_NotRejectedForMissingFlag()
    {
        var dir = MakePackage();
        var model = new ChallengeYamlModel { Name = "king-of-the-hill", Type = "KingOfTheHill" };

        var problems = ChallengeImportService.ValidateSubmissionShape(dir, ChallengeType.KingOfTheHill, model);

        // KotH has no flag (the platform reads /koth/king) — it must be exempt,
        // exactly like AttackDefense.
        Assert.DoesNotContain(problems, p => p.Contains("No flag declared"));
        // A complete KotH package (solver + Dockerfile, no flag needed) is clean.
        Assert.Empty(problems);
    }

    [Fact]
    public void ValidateSubmissionShape_AttackDefense_NotRejectedForMissingFlag()
    {
        var dir = MakePackage();
        var model = new ChallengeYamlModel { Name = "ad", Type = "AttackDefense" };

        var problems = ChallengeImportService.ValidateSubmissionShape(dir, ChallengeType.AttackDefense, model);

        Assert.DoesNotContain(problems, p => p.Contains("No flag declared"));
    }

    [Fact]
    public void ValidateSubmissionShape_StaticContainer_NoFlag_StillRejected()
    {
        // The exemption must stay narrow: a non-engine container type with no flag
        // source is still a real problem (UsesAdEngine() is false here).
        var dir = MakePackage();
        var model = new ChallengeYamlModel { Name = "web", Type = "StaticContainer" };

        var problems = ChallengeImportService.ValidateSubmissionShape(dir, ChallengeType.StaticContainer, model);

        Assert.Contains(problems, p => p.Contains("No flag declared"));
    }

    [Fact]
    public void ValidateSubmissionShape_Koth_MissingSolver_StillFlagged()
    {
        // The fix only touched the flag exemption — the solver requirement still
        // applies to KotH.
        var dir = MakePackage(withSolver: false);
        var model = new ChallengeYamlModel { Name = "king-of-the-hill", Type = "KingOfTheHill" };

        var problems = ChallengeImportService.ValidateSubmissionShape(dir, ChallengeType.KingOfTheHill, model);

        Assert.Contains(problems, p => p.Contains("solver"));
    }

    // ---- Push-back serializer: ad: block emitted for KotH (ChallengeYamlSerializer.cs:103) ----

    static GameChallenge KothChallenge() => new()
    {
        Title = "king-of-the-hill",
        Content = "hold the hill",
        Category = ChallengeCategory.Misc,
        Type = ChallengeType.KingOfTheHill,
        ContainerImage = "img:1",
        ExposePort = 80,
    };

    [Fact]
    public void Serialize_Koth_NonDefaultAd_EmitsAdBlock()
    {
        var ch = KothChallenge();
        ch.AdAllowEgress = false;     // a hill is a target — no outbound
        ch.AdAllowSelfReset = false;  // shared hill must forbid team self-reset

        var yaml = ChallengeYamlSerializer.Serialize(ch, []);

        Assert.Contains("type: KingOfTheHill", yaml);
        Assert.Contains("allowEgress: false", yaml);
        Assert.Contains("allowSelfReset: false", yaml);
    }

    [Fact]
    public void Serialize_Koth_DefaultAd_OmitsAdBlock()
    {
        // Defaults (egress + self-reset true, no checker) match the entity init,
        // so the ad: block is omitted — same non-churn behavior as A&D.
        var ch = KothChallenge(); // AdAllowEgress / AdAllowSelfReset default true

        var yaml = ChallengeYamlSerializer.Serialize(ch, []);

        Assert.DoesNotContain("allowEgress:", yaml);
        Assert.DoesNotContain("allowSelfReset:", yaml);
        Assert.DoesNotContain("checkerImage:", yaml);
    }
}
