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

    /// <summary>Total times this team's services were captured (raw count).</summary>
    public int TimesCaptured { get; set; }

    /// <summary>Total flags this team captured (raw count).</summary>
    public int FlagsCaptured { get; set; }

    /// <summary>Per-service breakdown, one entry per challenge in <see cref="AdScoreboardModel.Challenges"/> order.</summary>
    public List<AdServiceScore> Services { get; set; } = [];
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

    /// <summary>Flags this team captured on this service.</summary>
    public int FlagsCaptured { get; set; }

    /// <summary>Times this team's instance of this service was captured.</summary>
    public int TimesCaptured { get; set; }

    /// <summary>Latest check verdict for this team's service — Ok / Mumble / Offline / InternalError / null if never checked.</summary>
    public string? LastCheckStatus { get; set; }
}
