using System;
using System.Linq;
using GZCTF.Models.Internal;
using GZCTF.Services;
using Xunit;

namespace GZCTF.Test.UnitTests.Services;

/// <summary>
/// Pins the fair-scoring invariants of <see cref="SuspicionScoring.Compute" />:
/// network/identity (Context) signals never score on their own, low-tier signals
/// can never out-rank hard evidence, Details-drift is defused by the per-rule
/// incident cap, and the risk band is derived from the highest tier that fired.
/// See the fair cheat-scoring redesign.
/// </summary>
public class SuspicionScoringTests
{
    private static int DefaultWeight(string code) => SuspicionService.GetDefaultWeight(code);

    private static (string, string, DateTimeOffset, int) Evt(string type, string details, int minute = 0)
        => (type, details, new DateTimeOffset(2026, 6, 6, 0, minute, 0, TimeSpan.Zero), DefaultWeight(type));

    [Fact]
    public void Context_SignalsAlone_ScoreZero_AndBandIsContext()
    {
        // A CGNAT/campus team: a pile of distinct IP/identity events, nothing else.
        var events = new[]
        {
            Evt(SuspicionType.SharedIP, "ip=1.2.3.4 users=a,b"),
            Evt(SuspicionType.CrossTeamIP, "ip=1.2.3.4 chal=1", 1),
            Evt(SuspicionType.CrossTeamIP, "ip=1.2.3.4 chal=2", 2),
            Evt(SuspicionType.IpChurn, "user=a seen=...", 3),
            Evt(SuspicionType.UnknownIP, "ip=5.6.7.8", 4),
            Evt(SuspicionType.SubnetOverlap, "net=1.2.3.0/28", 5),
            Evt(SuspicionType.SessionConcurrency, "user=a", 6),
        };

        var b = SuspicionScoring.Compute(events, DefaultWeight);

        Assert.Equal(0, b.Total);
        Assert.Equal(0, b.Hard);
        Assert.Equal(0, b.Strong);
        Assert.Equal(0, b.Behavioral);
        Assert.Equal(0, b.Corroboration);
        Assert.Equal(RiskBand.Context, b.Band);
        Assert.All(b.Events, e => Assert.False(e.Counted));
    }

    [Fact]
    public void HardEvidence_AlwaysOutranks_AnyPileOfLowTierSignals()
    {
        // Worst-case innocent-looking pile: saturated strong + behavioral + context.
        var noisy = new[]
        {
            Evt(SuspicionType.AutomatedPattern, "a"), Evt(SuspicionType.HighWrongRate, "b", 1),
            Evt(SuspicionType.SolutionRelay, "c", 2), Evt(SuspicionType.FastSolveOpen, "d", 3),
            Evt(SuspicionType.Burst, "e", 4), Evt(SuspicionType.SharedIP, "f", 5),
            Evt(SuspicionType.CrossTeamIP, "g", 6), Evt(SuspicionType.SharedFingerprint, "h", 7),
        };
        var noisyScore = SuspicionScoring.Compute(noisy, DefaultWeight);

        // A single piece of real hard evidence, nothing else.
        var hard = new[] { Evt(SuspicionType.WrongFlagLeakage, "chal=1 hash=deadbeef") };
        var hardScore = SuspicionScoring.Compute(hard, DefaultWeight);

        // No hard evidence => never the EVIDENCED band, capped at 85.
        Assert.NotEqual(RiskBand.Evidenced, noisyScore.Band);
        Assert.True(noisyScore.Total <= 85, $"non-hard total {noisyScore.Total} exceeded ceiling");

        // The genuine single hard hit is EVIDENCED and ranks above the noisy team.
        Assert.Equal(RiskBand.Evidenced, hardScore.Band);
        Assert.True((int)hardScore.Band > (int)noisyScore.Band);
    }

    [Fact]
    public void DriftBomb_RepeatedDistinctDetails_IsBoundedByIncidentCap()
    {
        // 50 CollusionGroup events with ever-drifting Details (avgRsi=...) — the
        // exact write-on-read inflation the redesign defuses. Cap = 1.
        var drift = Enumerable.Range(0, 50)
            .Select(i => Evt(SuspicionType.CollusionGroup, $"avgRsi=0.{i:00} members=3", i))
            .ToArray();

        var b = SuspicionScoring.Compute(drift, DefaultWeight);

        var cap = SuspicionType.GetMaxIncidents(SuspicionType.CollusionGroup);
        Assert.Equal(1, cap);
        // Only `cap` incidents count; the rest are present but not scored.
        Assert.Equal(DefaultWeight(SuspicionType.CollusionGroup) * cap, b.Behavioral);
        Assert.Equal(cap, b.Events.Count(e => e.Counted));
    }

    [Fact]
    public void StrongAndBehavioral_AreClampedToTierCeilings()
    {
        var events = new[]
        {
            // Strong: HoneypotChain(150) + AutomatedPattern(50) -> clamps to 60
            Evt(SuspicionType.HoneypotChain, "x"), Evt(SuspicionType.AutomatedPattern, "y", 1),
            // Behavioral: a heap of fast-solves -> clamps to 25
            Evt(SuspicionType.FastSolveOpen, "1", 2), Evt(SuspicionType.FastSolveDownload, "2", 3),
            Evt(SuspicionType.FastSolveContainer, "3", 4), Evt(SuspicionType.ZeroWrongAttempts, "4", 5),
            Evt(SuspicionType.Burst, "5", 6),
        };

        var b = SuspicionScoring.Compute(events, DefaultWeight);

        Assert.Equal(60, b.Strong);
        Assert.Equal(25, b.Behavioral);
        Assert.Equal(0, b.Hard);
        Assert.Equal(RiskBand.Investigate, b.Band);
        Assert.Equal(85, b.Total);
    }

    [Fact]
    public void Corroboration_OnlyCountsWhenHardEvidencePresent_AndIsCappedAtHalfHard()
    {
        var context = new[]
        {
            Evt(SuspicionType.SharedFingerprint, "fp"),   // unit 20
            Evt(SuspicionType.CrossTeamIP, "ip", 1),      // unit 10
            Evt(SuspicionType.SessionConcurrency, "u", 2),// unit 10
        };

        // Without hard evidence: corroboration is zero.
        var noHard = SuspicionScoring.Compute(context, DefaultWeight);
        Assert.Equal(0, noHard.Corroboration);

        // With one StolenFlag (hard=100): corroboration = min(100/2, 20+10+10=40) = 40.
        var withHard = SuspicionScoring.Compute(
            context.Append(Evt(SuspicionType.StolenFlag, "victim=2 chal=1", 3)), DefaultWeight);
        Assert.Equal(100, withHard.Hard);
        Assert.Equal(40, withHard.Corroboration);
        Assert.Equal(RiskBand.Evidenced, withHard.Band);
    }

    [Fact]
    public void UnknownRuleCode_DefaultsToBehavioral_AndDoesNotThrow()
    {
        var events = new[] { Evt("SomeFutureSignal", "details") };
        var b = SuspicionScoring.Compute(events, _ => 10);

        Assert.Equal(SuspicionTier.Behavioral, SuspicionType.GetTier("SomeFutureSignal"));
        Assert.Equal(10, b.Behavioral);
        Assert.Equal(RiskBand.Watch, b.Band);
    }

    [Fact]
    public void EmptyEvents_AreClean()
    {
        var b = SuspicionScoring.Compute(Array.Empty<(string, string, DateTimeOffset, int)>(), DefaultWeight);
        Assert.Equal(RiskBand.Clean, b.Band);
        Assert.Equal(0, b.Total);
    }
}
