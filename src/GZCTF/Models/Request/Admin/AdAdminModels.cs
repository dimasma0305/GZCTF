using System.ComponentModel.DataAnnotations;
using GZCTF.Utils;

namespace GZCTF.Models.Request.Admin;

public class AdAdvanceRoundResult
{
    public int RoundNumber { get; set; }
    public int FlagsPlanted { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
}

public class AdGameStateModel
{
    public int? CurrentRound { get; set; }
    public DateTimeOffset? RoundStartedAt { get; set; }
    public DateTimeOffset? RoundEndsAt { get; set; }
    public bool ScoringPaused { get; set; }
    public List<AdChallengeStateModel> Challenges { get; set; } = [];
    public List<AdTeamRowModel> Teams { get; set; } = [];
}

public class AdChallengeStateModel
{
    public int ChallengeId { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public int TickSeconds { get; set; }
    public int FlagLifetimeTicks { get; set; }
    public int? TeamsWithLiveContainer { get; set; }
}

public class AdTeamRowModel
{
    public int ParticipationId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public List<AdTeamCellModel> Services { get; set; } = [];
}

public class AdTeamCellModel
{
    public int AdTeamServiceId { get; set; }
    public int ChallengeId { get; set; }
    public string? ContainerIp { get; set; }
    public int? ContainerPort { get; set; }
    public string? LastCheckStatus { get; set; }
    public string? CurrentFlag { get; set; }
}

public class AdOverrideCheckModel
{
    [Required]
    public AdCheckStatus NewStatus { get; set; }

    [MaxLength(2048)]
    public string? Note { get; set; }
}
