using System;
using GZCTF.Models.Data;
using GZCTF.Utils;
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

    /// <summary>The exact historical formula, inlined here as the reference the refactored
    /// <see cref="ScoreCurve.Standard"/> path must reproduce bit-for-bit.</summary>
    private static int LegacyStandardFormula(int originalScore, double minScoreRate, double difficulty,
        int acceptedCount)
    {
        if (acceptedCount <= 1)
            return originalScore;

        return (int)Math.Floor(
            originalScore *
            (minScoreRate + (1.0 - minScoreRate) * Math.Exp((1 - acceptedCount) / difficulty)));
    }

    [Fact]
    public void StandardCurve_IsByteIdenticalToLegacyFormula_AcrossFullInputSweep()
    {
        // Pluggable score curves refactored CalculateChallengeScore; the default Standard
        // curve MUST be indistinguishable from the pre-refactor formula, or every live
        // scoreboard shifts silently. Sweep the full valid input space and assert exact
        // equality (both the explicit ScoreCurve.Standard arg and the defaulted overload).
        foreach (var original in new[] { 0, 1, 100, 500, 1000, 5000 })
        foreach (var minRate in new[] { 0.0, 0.1, 0.25, 0.5, 0.8, 1.0 })
        foreach (var difficulty in new[] { 0.01, 0.5, 1.0, 3.0, 5.0, 50.0, 100.0 })
        foreach (var n in new[] { 0, 1, 2, 3, 5, 10, 50, 300, 100000 })
        {
            var expected = LegacyStandardFormula(original, minRate, difficulty, n);
            Assert.Equal(expected,
                GameChallenge.CalculateChallengeScore(original, minRate, difficulty, n, ScoreCurve.Standard));
            // The parameterless-curve overload must also default to Standard.
            Assert.Equal(expected,
                GameChallenge.CalculateChallengeScore(original, minRate, difficulty, n));
        }
    }

    [Theory]
    [InlineData(ScoreCurve.Standard)]
    [InlineData(ScoreCurve.Linear)]
    [InlineData(ScoreCurve.Logarithmic)]
    public void AllCurves_DecayMonotonically_WithinFloorAndOriginal(ScoreCurve curve)
    {
        // Every curve shares the invariants: full points at <=1 solve, non-increasing in
        // acceptedCount, never above OriginalScore, never below the floor (Original*MinRate),
        // finite. Only the shape between the endpoints differs.
        Assert.Equal(1000, GameChallenge.CalculateChallengeScore(1000, 0.25, 5.0, 1, curve));

        var prev = int.MaxValue;
        for (var n = 1; n <= 500; n++)
        {
            var score = GameChallenge.CalculateChallengeScore(1000, 0.25, 5.0, n, curve);
            Assert.InRange(score, 250, 1000);
            Assert.True(score <= prev, $"{curve}: score must not increase at n={n}: {score} > {prev}");
            prev = score;
        }
    }

    [Theory]
    [InlineData(ScoreCurve.Linear)]
    [InlineData(ScoreCurve.Logarithmic)]
    public void NewCurves_StayFiniteAndBounded_AtExtremes(ScoreCurve curve)
    {
        // Extreme difficulty + huge solve counts must still yield a finite integer in
        // [floor, original] — no NaN/Infinity, no under/overflow past the bounds.
        Assert.InRange(GameChallenge.CalculateChallengeScore(1000, 0.25, 0.01, 100000, curve), 250, 1000);
        Assert.InRange(GameChallenge.CalculateChallengeScore(1000, 0.0, 100.0, 100000, curve), 0, 1000);
        Assert.InRange(GameChallenge.CalculateChallengeScore(1000, 0.25, 100.0, 2, curve), 250, 1000);
    }
}
