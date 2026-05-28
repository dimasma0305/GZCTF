using System;
using GZCTF.Utils;
using Xunit;

namespace GZCTF.Test.UnitTests.Utils;

/// <summary>
/// Tests for the A&amp;D scoring formulas (<see cref="AdScoring"/>) — the single
/// source of truth shared by the scoreboard, timeline, and per-tick SLA credit.
/// </summary>
public class AdScoringTests
{
    [Theory]
    [InlineData(0, 10.0)]        // first blood — full base
    [InlineData(1, 7.0710678)]   // 10 / sqrt(2)
    [InlineData(3, 5.0)]         // 10 / sqrt(4)
    public void AttackPoints_FirstBloodWeighted(int priorCapturers, double expected)
        => Assert.Equal(expected, AdScoring.AttackPoints(priorCapturers), 6);

    [Fact]
    public void AttackPoints_DecreasesWithMoreCapturers()
    {
        Assert.True(AdScoring.AttackPoints(0) > AdScoring.AttackPoints(1));
        Assert.True(AdScoring.AttackPoints(1) > AdScoring.AttackPoints(5));
    }

    [Theory]
    [InlineData(0, 0.0)]    // never captured → no loss
    [InlineData(1, 2.0)]    // 2 * 1^0.75
    [InlineData(16, 16.0)]  // 2 * 16^0.75 = 2 * 8
    public void DefenseLoss_SubLinearPenalty(int timesCaptured, double expected)
        => Assert.Equal(expected, AdScoring.DefenseLoss(timesCaptured), 6);

    [Theory]
    [InlineData(4, 2.0)]    // sqrt(4)
    [InlineData(0, 1.0)]    // sqrt(max(1, 0)) = 1 → no divide-by-zero
    [InlineData(9, 3.0)]
    public void SlaFieldFactor_SqrtTeams(int teams, double expected)
        => Assert.Equal(expected, AdScoring.SlaFieldFactor(teams), 6);

    [Theory]
    [InlineData(10.0, 10.0)] // SLA is just the SUM of already-field-scaled credit
    [InlineData(0.0, 0.0)]
    public void SlaPoints_SumsStoredCredit(double creditSum, double expected)
        => Assert.Equal(expected, AdScoring.SlaPoints(creditSum), 6);

    [Theory]
    [InlineData(AdCheckStatus.Ok, null, 1.0)]                       // clean Ok (first tick)
    [InlineData(AdCheckStatus.Ok, AdCheckStatus.Ok, 1.0)]           // sustained Ok
    [InlineData(AdCheckStatus.Ok, AdCheckStatus.Offline, 0.5)]      // recovering from down
    [InlineData(AdCheckStatus.Ok, AdCheckStatus.Mumble, 0.5)]       // recovering from mumble
    [InlineData(AdCheckStatus.Ok, AdCheckStatus.InternalError, 1.0)]// checker fault isn't the team's down
    [InlineData(AdCheckStatus.Offline, AdCheckStatus.Ok, 0.0)]
    [InlineData(AdCheckStatus.Mumble, AdCheckStatus.Ok, 0.0)]
    [InlineData(AdCheckStatus.InternalError, AdCheckStatus.Ok, 0.0)]
    public void TickCredit_RecoveringEarnsHalf(AdCheckStatus current, AdCheckStatus? previous, double expected)
        => Assert.Equal(expected, AdScoring.TickCredit(current, previous), 6);

    // KotH hold credit is flat per-tick — no team-count scaling — so the
    // displayed cell value is a clean integer per tick ("hold one tick = +1")
    // instead of the fractional 1×sqrt(teams) the previous SLA-style scaling
    // produced (e.g. 1.73 on a 3-team game).
    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(2.0, 2.0)]
    [InlineData(0.5, 0.5)]
    public void KothHoldPoints_FlatPerTick(double perTick, double expected)
        => Assert.Equal(expected, AdScoring.KothHoldPoints(perTick), 6);

    [Fact]
    public void KothTickDelta_NoKing_IsZero()
    {
        var (hold, pen) = AdScoring.KothTickDelta(hasKing: false, AdCheckStatus.Ok, 1.0);
        Assert.Equal(0.0, hold);
        Assert.Equal(0.0, pen);
    }

    [Fact]
    public void KothTickDelta_FunctionalKing_EarnsHoldNoPenalty()
    {
        var (hold, pen) = AdScoring.KothTickDelta(hasKing: true, AdCheckStatus.Ok, 1.0);
        Assert.Equal(1.0, hold, 6); // flat per-tick
        Assert.Equal(0.0, pen);
    }

    [Theory]
    [InlineData(AdCheckStatus.Mumble)]
    [InlineData(AdCheckStatus.Offline)]
    public void KothTickDelta_KingOnBrokenHill_EatsPenaltyNoHold(AdCheckStatus status)
    {
        var (hold, pen) = AdScoring.KothTickDelta(hasKing: true, status, 1.0);
        Assert.Equal(0.0, hold);
        Assert.Equal(AdScoring.KothBrokenHillPenalty, pen, 6);
    }

    [Fact]
    public void KothTickDelta_KingOnInternalError_NoPenalty()
    {
        // A checker/infra fault (pruned image, no network) is NOT the holder's
        // fault — like the SLA path it must never debit them (no hold, no penalty).
        var (hold, pen) = AdScoring.KothTickDelta(hasKing: true, AdCheckStatus.InternalError, 1.0);
        Assert.Equal(0.0, hold);
        Assert.Equal(0.0, pen);
    }

    [Fact]
    public void KothTickDelta_FreshlyElectedOnBrokenHill_GraceTick()
    {
        // L4 audit fix — a team that just took over a broken hill gets a
        // one-tick grace (the previous holder broke it; not their fault yet).
        var (hold, pen) = AdScoring.KothTickDelta(
            hasKing: true, AdCheckStatus.Offline, 1.0, freshlyElected: true);
        Assert.Equal(0.0, hold);
        Assert.Equal(0.0, pen);
    }
}
