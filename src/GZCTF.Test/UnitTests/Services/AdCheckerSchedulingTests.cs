using System;
using System.Linq;
using GZCTF.Models.Data;
using GZCTF.Services;
using Xunit;

namespace GZCTF.Test.UnitTests.Services;

/// <summary>
/// Tests for the getflag scheduling jitter — the per-(service, round) randomized
/// SLA-check time that keeps teams from predicting (and hiding a popped box only
/// during) the check, while staying inside the tick.
/// </summary>
public class AdCheckerSchedulingTests
{
    [Fact]
    public void StableJitterFraction_IsDeterministic()
        => Assert.Equal(
            AdCheckerService.StableJitterFraction(7, 42),
            AdCheckerService.StableJitterFraction(7, 42));

    [Fact]
    public void StableJitterFraction_StaysInUnitInterval()
    {
        for (var s = 1; s <= 50; s++)
        for (var r = 1; r <= 50; r++)
        {
            var f = AdCheckerService.StableJitterFraction(s, r);
            Assert.True(f >= 0.0 && f < 1.0, $"fraction {f} out of [0,1) for ({s},{r})");
        }
    }

    [Fact]
    public void StableJitterFraction_VariesAcrossRounds()
    {
        // Re-rolled each round so the schedule can't be fingerprinted.
        var distinct = Enumerable.Range(1, 25)
            .Select(r => AdCheckerService.StableJitterFraction(7, r))
            .Distinct().Count();
        Assert.True(distinct > 1, "jitter should vary across rounds");
    }

    [Fact]
    public void GetflagDueAt_NeverBeforeGrace_AndLeavesAPollBeforeRoundEnd()
    {
        var start = DateTimeOffset.UtcNow;
        const double tick = 60, poll = 5;
        const int grace = 3;
        var round = new AdRound { Id = 9, StartedAt = start, EndsAt = start.AddSeconds(tick) };

        for (var sid = 1; sid <= 100; sid++)
        {
            var ts = new AdTeamService { Id = sid };
            var due = AdCheckerService.GetflagDueAt(ts, round, tick, poll, grace, 0.5);
            Assert.True(due >= start.AddSeconds(grace), $"due fired before grace for sid={sid}");
            Assert.True(due <= start.AddSeconds(tick - poll), $"due left < one poll before round end for sid={sid}");
        }
    }

    [Fact]
    public void GetflagDueAt_CapsGraceAtHalfTick()
    {
        // grace 100s with a 60s tick → capped at 30s; getFrac 0 → no jitter → due
        // is exactly start + 30s.
        var start = DateTimeOffset.UtcNow;
        var round = new AdRound { Id = 1, StartedAt = start, EndsAt = start.AddSeconds(60) };
        var ts = new AdTeamService { Id = 1 };

        var due = AdCheckerService.GetflagDueAt(ts, round,
            tickSeconds: 60, pollSeconds: 5, graceSeconds: 100, getflagFraction: 0.0);

        Assert.Equal(start.AddSeconds(30), due);
    }
}
