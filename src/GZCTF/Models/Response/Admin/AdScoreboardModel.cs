namespace GZCTF.Models.Response.Admin;

/// <summary>
/// Body returned by GET /api/Game/{id}/Ad/Scoreboard. FAUST-ish per-team
/// aggregate plus per-service breakdown — independent of the jeopardy
/// scoreboard. UI shows it under a separate tab when the game has both
/// kinds of challenges; standalone otherwise.
/// </summary>
public sealed class AdScoreboardModel
{
    public int LatestRound { get; set; }
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<AdTeamScoreRow> Teams { get; set; } = [];
}

public sealed class AdTeamScoreRow
{
    public int Rank { get; set; }
    public int ParticipationId { get; set; }
    public int TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string? Division { get; set; }
    public double Total { get; set; }

    /// <summary>Sum of all AdAttack.Points captured by this team.</summary>
    public double AttackPoints { get; set; }

    /// <summary>Penalty: how many distinct (team-service, round) pairs were captured FROM this team.</summary>
    public double DefenseLoss { get; set; }

    /// <summary>SLA fraction × scaling — proportional to (Ok / total checks).</summary>
    public double SlaPoints { get; set; }

    /// <summary>How many times this team's services were captured (raw count).</summary>
    public int TimesCaptured { get; set; }

    /// <summary>How many flags this team captured (raw count).</summary>
    public int FlagsCaptured { get; set; }
}
