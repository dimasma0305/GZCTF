using MemoryPack;

namespace GZCTF.Models.Response.Admin;

/// <summary>
/// Body returned by GET /api/Game/{id}/Ad/Scoreboard. Scoring is per-service
/// (FAUST/EnoEngine model): each <see cref="AdTeamScoreRow"/> carries a
/// per-challenge breakdown in <see cref="AdTeamScoreRow.Services"/>, and the
/// team total is the sum of the service nets. <see cref="Challenges"/> gives
/// the column order so the UI can render one column per service (mirroring
/// the jeopardy board). Independent of the jeopardy scoreboard.
///
/// <para>Cached via <c>CacheMaker</c> (MemoryPack-serialized) like the jeopardy
/// board, so it isn't recomputed per request — hence the MemoryPackable types.</para>
/// </summary>
[MemoryPackable]
public sealed partial class AdScoreboardModel
{
    public int LatestRound { get; set; }
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>True when this response is an ICPC-freeze snapshot (caller is a non-monitor and now ∈ [freeze, end)).</summary>
    public bool IsFrozenView { get; set; }

    /// <summary>The game's configured freeze time, surfaced so the UI can render a "frozen as of …" banner.</summary>
    public DateTimeOffset? Freeze { get; set; }

    /// <summary>Enabled A&amp;D challenges, in display order — the scoreboard's per-service columns.</summary>
    public List<AdScoreboardChallenge> Challenges { get; set; } = [];

    public List<AdTeamScoreRow> Teams { get; set; } = [];
}

/// <summary>One A&amp;D challenge (= one service column-group on the scoreboard).</summary>
[MemoryPackable]
public sealed partial class AdScoreboardChallenge
{
    public int ChallengeId { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>Category name (Web / Pwn / …) — drives the colored category tier, mirroring the jeopardy board.</summary>
    public string Category { get; set; } = string.Empty;
}

[MemoryPackable]
public sealed partial class AdTeamScoreRow
{
    public int Rank { get; set; }
    public int ParticipationId { get; set; }
    public int TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string? Division { get; set; }

    /// <summary>Team total = sum of <see cref="AdServiceScore.Net"/> across services.</summary>
    public double Total { get; set; }

    /// <summary>Aggregate attack points across all services.</summary>
    public double AttackPoints { get; set; }

    /// <summary>Aggregate defense loss — computed per-service then summed (Σ caps_s^0.75 × scale).</summary>
    public double DefenseLoss { get; set; }

    /// <summary>Aggregate SLA points across all services.</summary>
    public double SlaPoints { get; set; }

    /// <summary>Aggregate King-of-the-Hill hold points (Σ HoldCredit − Penalty) across all hills.</summary>
    public double KothPoints { get; set; }

    /// <summary>Total times this team's services were captured (raw count).</summary>
    public int TimesCaptured { get; set; }

    /// <summary>Total flags this team captured (raw count).</summary>
    public int FlagsCaptured { get; set; }

    /// <summary>Per-service breakdown, one entry per challenge in <see cref="AdScoreboardModel.Challenges"/> order.</summary>
    public List<AdServiceScore> Services { get; set; } = [];
}

/// <summary>
/// Body returned by GET /api/Game/{id}/Ad/Koth/Scoreboard — KotH-only view.
/// Same shape as <see cref="AdScoreboardModel"/> but stripped to the hill
/// columns (no A&amp;D services, no attack/defense/SLA breakdown). Useful when
/// the game is KotH-dominant and the combined scoreboard's empty A&amp;D columns
/// would just be noise. Also lets the UI render a dedicated KotH page that
/// can show the live holder per hill — something the combined board doesn't.
///
/// <para>Cached separately from the combined board (key
/// <c>_KothScoreboard_&lt;id&gt;</c>); shares the same invalidation trigger
/// (<see cref="Services.Cache.CacheHelper.FlushAdScoreboardCache"/>) because
/// the same events (round-advance + checker tick) move both boards.</para>
/// </summary>
[MemoryPackable]
public sealed partial class KothScoreboardModel
{
    public int LatestRound { get; set; }
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsFrozenView { get; set; }
    public DateTimeOffset? Freeze { get; set; }

    /// <summary>Enabled KotH hills in display order (one per challenge).</summary>
    public List<KothScoreboardHill> Hills { get; set; } = [];

    public List<KothTeamScoreRow> Teams { get; set; } = [];
}

/// <summary>One KotH challenge column on the dedicated KotH board.</summary>
[MemoryPackable]
public sealed partial class KothScoreboardHill
{
    public int ChallengeId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Team name currently holding this hill (matches last persisted KothControlResult).
    /// Null when nobody holds it this tick. The per-team-row's <c>isCurrentHolder</c>
    /// flag (on <see cref="KothHillScore"/>) tells the UI whether the row IS the holder,
    /// so the participation id isn't needed on the wire.
    /// </summary>
    public string? CurrentHolderTeamName { get; set; }

    /// <summary>Latest functional verdict for the hill (shared, not per-team).</summary>
    public string? LastCheckStatus { get; set; }

    /// <summary>Round at which the hill was last refreshed (5-tick wipe). 0 = never.</summary>
    public int LastRefreshRound { get; set; }
}

[MemoryPackable]
public sealed partial class KothTeamScoreRow
{
    public int Rank { get; set; }
    public int ParticipationId { get; set; }
    public int TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string? Division { get; set; }

    /// <summary>Sum of <see cref="KothHillScore.Points"/> across hills — what the rank is keyed on.</summary>
    public double Total { get; set; }

    /// <summary>One entry per hill in <see cref="KothScoreboardModel.Hills"/> order.</summary>
    public List<KothHillScore> Hills { get; set; } = [];
}

/// <summary>One team's score on one KotH hill (board cell).</summary>
[MemoryPackable]
public sealed partial class KothHillScore
{
    public int ChallengeId { get; set; }

    /// <summary>Σ (HoldCredit − Penalty) across every tick of this hill the team controlled — the net the row's Total is summed from.</summary>
    public double Points { get; set; }

    /// <summary>Σ HoldCredit only — the positive earned over every Ok tick the team held. Surfaced so the UI can show the gross alongside the penalty for visibility.</summary>
    public double Earned { get; set; }

    /// <summary>Σ Penalty only — the negative debited over every broken-hill tick the team held (after the freshly-elected grace). Always &gt;= 0; subtract from <see cref="Earned"/> to recover <see cref="Points"/>.</summary>
    public double Penalty { get; set; }

    /// <summary>Number of distinct ticks this team held the hill (any status).</summary>
    public int TicksHeld { get; set; }

    /// <summary>Number of those ticks where the hill was broken — what produced the Penalty above (a freshly-elected grace tick counts toward TicksHeld but NOT toward this count).</summary>
    public int BrokenTicks { get; set; }

    /// <summary>True when this team is the holder this tick (matches the latest persisted KothControlResult for the hill).</summary>
    public bool IsCurrentHolder { get; set; }
}

/// <summary>One team's score on one A&amp;D service (scoreboard cell).</summary>
[MemoryPackable]
public sealed partial class AdServiceScore
{
    public int ChallengeId { get; set; }

    /// <summary>Net contribution of this service to the team total: Attack + Sla − DefenseLoss.</summary>
    public double Net { get; set; }

    public double AttackPoints { get; set; }
    public double DefenseLoss { get; set; }
    public double SlaPoints { get; set; }

    /// <summary>King-of-the-Hill hold points on this hill (0 for A&amp;D services).</summary>
    public double KothPoints { get; set; }

    /// <summary>True when this column is a King-of-the-Hill hill (shared target) rather than an A&amp;D service.</summary>
    public bool IsKoth { get; set; }

    /// <summary>Flags this team captured on this service.</summary>
    public int FlagsCaptured { get; set; }

    /// <summary>Times this team's instance of this service was captured.</summary>
    public int TimesCaptured { get; set; }

    /// <summary>Latest check verdict for this team's service — Ok / Mumble / Offline / InternalError / null if never checked.</summary>
    public string? LastCheckStatus { get; set; }
}
