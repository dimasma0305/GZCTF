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
    [InlineData(5.0, 4, 10.0)]  // 5 * sqrt(4)
    [InlineData(3.0, 0, 3.0)]   // sqrt(max(1, 0)) = 1 → no divide-by-zero
    [InlineData(0.0, 100, 0.0)]
    public void SlaPoints_ScaledByFieldSize(double creditSum, int teams, double expected)
        => Assert.Equal(expected, AdScoring.SlaPoints(creditSum, teams), 6);

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
}
