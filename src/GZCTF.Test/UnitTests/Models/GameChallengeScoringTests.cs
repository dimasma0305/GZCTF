using GZCTF.Models.Data;
using Xunit;

namespace GZCTF.Test.UnitTests.Models;

/// <summary>
/// Regression tests for the jeopardy dynamic-score curve
/// (<see cref="GameChallenge.CalculateChallengeScore" />), pinning the
/// scoring-audit invariants: within the enforced input bounds (OriginalScore >= 0,
/// MinScoreRate in [0,1], Difficulty >= 0.01 — guarded by ChallengeUpdateModel /
/// ChallengeYaml clamp / TransferChallenge [Range]) the curve must never produce a
/// negative score, a score ABOVE OriginalScore (the inverted/inflating-curve bug),
/// or a NaN/Infinity. See project_scoring_audit_fixes.
/// </summary>
public class GameChallengeScoringTests
{
    [Theory]
    [InlineData(0)] // unsolved
    [InlineData(1)] // first solve — full points, no decay yet
    public void Score_FirstSolveOrUnsolved_IsOriginal(int acceptedCount)
        => Assert.Equal(1000, GameChallenge.CalculateChallengeScore(1000, 0.25, 5.0, acceptedCount));

    [Fact]
    public void Score_DecaysMonotonically_AndStaysWithinFloorAndOriginal()
    {
        // Non-increasing in acceptedCount, never above OriginalScore, never below
        // the floor (Original * MinScoreRate).
        var prev = int.MaxValue;
        for (var n = 1; n <= 300; n++)
        {
            var score = GameChallenge.CalculateChallengeScore(1000, 0.25, 5.0, n);
            Assert.InRange(score, 250, 1000); // floor 1000*0.25 .. original 1000
            Assert.True(score <= prev, $"score must not increase: n={n} gave {score} > {prev}");
            prev = score;
        }
    }

    [Fact]
    public void Score_AtMinDifficultyFloor_IsFiniteAndBounded()
    {
        // Difficulty's enforced minimum (0.01) decays almost instantly but must
        // still be a finite, bounded integer — NOT NaN/Infinity (the divide-by /
        // near-zero-difficulty concern behind fix #9 + the round-3 import gap).
        var score = GameChallenge.CalculateChallengeScore(1000, 0.25, 0.01, 50);
        Assert.InRange(score, 250, 1000);
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(5.0)]
    [InlineData(100.0)]
    public void Score_NeverExceedsOriginal_ForValidDifficulty(double difficulty)
    {
        // The #9 bug class: a non-positive/inverted difficulty makes exp() > 1 and
        // inflates the score above OriginalScore. With difficulty >= 0.01 enforced
        // on every write path, that can never happen.
        foreach (var n in new[] { 2, 5, 50, 500 })
            Assert.True(GameChallenge.CalculateChallengeScore(1000, 0.25, difficulty, n) <= 1000);
    }

    [Fact]
    public void Score_ZeroMinRate_FloorIsZero_NotNegative()
    {
        // MinScoreRate = 0 (lower bound) → the curve floors at 0, never negative.
        var score = GameChallenge.CalculateChallengeScore(1000, 0.0, 5.0, 100000);
        Assert.InRange(score, 0, 1000);
    }
}
