namespace GZCTF.Utils;

/// <summary>
/// Pure helpers for the King-of-the-Hill refresh-window math — the single source of
/// truth shared by token minting (<c>AdRoundService.AdvanceAsync</c>), marker matching
/// (<c>AdCheckerService</c>), and the token endpoint (<c>AdGameController</c>).
///
/// <para>
/// The control token is GAME-WIDE and minted ONCE per refresh window, at the window's
/// <b>anchor</b> round (every <c>KothRefreshTicks</c> rounds, when the hills reset). It
/// is keyed by (participation, anchor round) and stays stable across the window, so a
/// token planted anywhere in the window resolves to the same row. Minting and
/// resolution therefore have to agree exactly on where a window starts — keeping the
/// arithmetic in one place stops the three call sites from drifting (each used to carry
/// its own inline copy of this formula).
/// </para>
/// </summary>
public static class KothWindow
{
    /// <summary>
    /// The anchor (first) round of the refresh window that <paramref name="round"/>
    /// falls in — the round a window's token is keyed to. For <c>refreshTicks = 5</c>:
    /// rounds 1–5 → 1, rounds 6–10 → 6, rounds 11–15 → 11, … so any round in a window
    /// maps to that window's start.
    /// </summary>
    /// <param name="round">A 1-based round number. Values &lt;= 0 (warmup / no round yet)
    /// are returned unchanged — there is no window before round 1.</param>
    /// <param name="refreshTicks">Rounds per refresh window; clamped to a minimum of 1
    /// to match the <c>Math.Max(1, Game.KothRefreshTicks ?? 5)</c> load path, so a
    /// mis-set 0/negative value degrades to "every round is its own anchor" rather than
    /// dividing by zero.</param>
    public static int AnchorRound(int round, int refreshTicks)
    {
        if (round <= 0)
            return round;
        var ticks = refreshTicks < 1 ? 1 : refreshTicks;
        return (round - 1) / ticks * ticks + 1;
    }

    /// <summary>
    /// True when <paramref name="round"/> is a refresh-window boundary — the round at
    /// which a fresh token is minted and the hills reset. Equivalent to
    /// <c>AnchorRound(round, refreshTicks) == round</c>. For <c>refreshTicks = 5</c>:
    /// rounds 1, 6, 11, 16, … A round &lt;= 0 is never a boundary.
    /// </summary>
    public static bool IsMintBoundary(int round, int refreshTicks)
    {
        if (round <= 0)
            return false;
        var ticks = refreshTicks < 1 ? 1 : refreshTicks;
        return (round - 1) % ticks == 0;
    }
}
