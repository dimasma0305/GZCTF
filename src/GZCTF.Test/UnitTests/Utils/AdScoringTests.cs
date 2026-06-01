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
    // Attack is a RARITY POOL: each flag is worth AttackPool total, split among the
    // teams that stole it, so a capturer of a flag k teams took gets AttackPool/k.
    [Theory]
    [InlineData(1, 1.0)]          // only you → the whole pool
    [InlineData(2, 0.5)]          // shared by 2 → half each
    [InlineData(4, 0.25)]         // shared by 4 → quarter each
    [InlineData(0, 0.0)]          // non-positive count → 0 (no divide-by-zero)
    public void AttackShare_RarityPool(int capturers, double expected)
        => Assert.Equal(expected, AdScoring.AttackShare(capturers), 6);

    [Fact]
    public void AttackShare_DecreasesWithMoreCapturers()
    {
        Assert.True(AdScoring.AttackShare(1) > AdScoring.AttackShare(2));
        Assert.True(AdScoring.AttackShare(2) > AdScoring.AttackShare(5));
    }

    // Defense is the linear mirror: DefensePool per distinct compromised flag,
    // counted once per flag (not per capture).
    [Theory]
    [InlineData(0, 0.0)]    // never leaked → no loss
    [InlineData(1, 1.0)]    // one leaked flag
    [InlineData(16, 16.0)]  // sixteen leaked flags
    public void DefenseLoss_LinearPerCompromisedFlag(int compromisedFlags, double expected)
        => Assert.Equal(expected, AdScoring.DefenseLoss(compromisedFlags), 6);

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
