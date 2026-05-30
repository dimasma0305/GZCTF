using System.Linq;
using GZCTF.Utils;
using Xunit;

namespace GZCTF.Test.UnitTests.Utils;

/// <summary>
/// Tests for <see cref="KothWindow"/> — the refresh-window arithmetic shared by KotH
/// token minting, marker matching, and the token endpoint. These pin the invariant
/// that minting (<see cref="KothWindow.IsMintBoundary"/>) and resolution
/// (<see cref="KothWindow.AnchorRound"/>) agree on where a window starts; a past
/// divergence between the (then-inline) copies of this math caused a duplicate-key
/// crash and two HTTP 500s, so the property test below is the real regression guard.
/// </summary>
public class KothWindowTests
{
    // ---- AnchorRound: which round a window's token is keyed to ----

    [Theory]
    // refreshTicks = 5 → windows [1..5], [6..10], [11..15], …
    [InlineData(1, 5, 1)]
    [InlineData(2, 5, 1)]
    [InlineData(5, 5, 1)]
    [InlineData(6, 5, 6)]
    [InlineData(10, 5, 6)]
    [InlineData(11, 5, 11)]
    [InlineData(1107, 5, 1106)] // a real round seen live mid-session
    // refreshTicks = 1 → every round is its own window
    [InlineData(1, 1, 1)]
    [InlineData(42, 1, 42)]
    // refreshTicks = 3
    [InlineData(3, 3, 1)]
    [InlineData(4, 3, 4)]
    [InlineData(9, 3, 7)]
    public void AnchorRound_MapsRoundToWindowStart(int round, int refreshTicks, int expected)
        => Assert.Equal(expected, KothWindow.AnchorRound(round, refreshTicks));

    [Theory]
    [InlineData(0, 5)]   // warmup — no round yet
    [InlineData(-1, 5)]  // defensive: negative passes through unchanged
    public void AnchorRound_NonPositiveRound_ReturnedUnchanged(int round, int refreshTicks)
        => Assert.Equal(round, KothWindow.AnchorRound(round, refreshTicks));

    [Theory]
    [InlineData(7, 0)]   // a mis-set 0 must not divide-by-zero
    [InlineData(7, -3)]  // negative clamps to 1 → every round its own anchor
    public void AnchorRound_RefreshTicksClampedToOne(int round, int refreshTicks)
        => Assert.Equal(round, KothWindow.AnchorRound(round, refreshTicks));

    // ---- IsMintBoundary: which rounds mint a fresh token ----

    [Theory]
    [InlineData(1, 5, true)]
    [InlineData(2, 5, false)]
    [InlineData(5, 5, false)]
    [InlineData(6, 5, true)]
    [InlineData(11, 5, true)]
    [InlineData(1, 1, true)]   // every round is a boundary when refreshTicks = 1
    [InlineData(42, 1, true)]
    public void IsMintBoundary_FiresOnlyAtWindowStart(int round, int refreshTicks, bool expected)
        => Assert.Equal(expected, KothWindow.IsMintBoundary(round, refreshTicks));

    [Theory]
    [InlineData(0, 5)]   // warmup is never a mint boundary
    [InlineData(-1, 5)]
    public void IsMintBoundary_NonPositiveRound_False(int round, int refreshTicks)
        => Assert.False(KothWindow.IsMintBoundary(round, refreshTicks));

    [Fact]
    public void IsMintBoundary_RefreshTicksZero_NoDivideByZero()
        => Assert.True(KothWindow.IsMintBoundary(7, 0)); // clamps to 1 → boundary

    // ---- The cross-consistency invariant (the one that actually matters) ----

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void IsMintBoundary_IffAnchorEqualsRound(int refreshTicks)
    {
        // A round mints a token exactly when it IS its own window anchor. If these two
        // ever disagree, a token gets minted at a round the resolver never looks up
        // (resolver returns null → "no controller"), or the resolver looks up a round
        // nothing minted (the live failure mode). They must be identical for all rounds.
        for (var round = 1; round <= 200; round++)
        {
            var minted = KothWindow.IsMintBoundary(round, refreshTicks);
            var isAnchor = KothWindow.AnchorRound(round, refreshTicks) == round;
            Assert.Equal(isAnchor, minted);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(8)]
    public void AnchorRound_IsStableAcrossAWholeWindow(int refreshTicks)
    {
        // Every round in a window must resolve to the SAME anchor — that stability is
        // what lets a team plant once after a reset and hold the window. Verify each
        // contiguous run of `refreshTicks` rounds shares one anchor, and that anchor is
        // itself a mint boundary.
        foreach (var anchor in Enumerable.Range(0, 40).Select(w => w * refreshTicks + 1))
        {
            Assert.True(KothWindow.IsMintBoundary(anchor, refreshTicks));
            for (var offset = 0; offset < refreshTicks; offset++)
                Assert.Equal(anchor, KothWindow.AnchorRound(anchor + offset, refreshTicks));
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(3)]
    public void AnchorRound_TokenFromPreviousWindowDoesNotResolveIntoTheNext(int refreshTicks)
    {
        // A token is keyed to its window anchor; once the next window starts, the new
        // current round resolves to a DIFFERENT anchor, so last window's row stops
        // matching (the "stale token is dead after a reset" rule).
        for (var anchor = 1; anchor <= 100; anchor += refreshTicks)
        {
            var nextAnchor = anchor + refreshTicks;
            Assert.NotEqual(KothWindow.AnchorRound(anchor, refreshTicks),
                            KothWindow.AnchorRound(nextAnchor, refreshTicks));
        }
    }
}
