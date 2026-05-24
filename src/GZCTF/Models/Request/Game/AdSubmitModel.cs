using System.ComponentModel.DataAnnotations;

namespace GZCTF.Models.Request.Game;

/// <summary>
/// Body for POST /api/game/{id}/ad/submit — attack submission.
/// </summary>
public class AdSubmitModel
{
    /// <summary>The flag string the attacker captured from a victim's service.</summary>
    [Required]
    [MaxLength(Limits.MaxFlagLength)]
    public string Flag { get; set; } = string.Empty;
}

/// <summary>
/// Response from a successful attack submission.
/// </summary>
public class AdSubmitResultModel
{
    /// <summary>"accepted" / "duplicate" / "wrong" / "expired" / "self_attack" / "not_started".</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Points awarded for this capture (only set when Status = accepted).</summary>
    public double? Points { get; set; }

    /// <summary>Round in which the captured flag was originally planted.</summary>
    public int? FlagPlantedAtRound { get; set; }

    /// <summary>Optional human-readable message.</summary>
    public string? Message { get; set; }
}

/// <summary>
/// Response shape for GET /api/game/{id}/ad/state — what the player sees on the
/// A&amp;D tab of their game page.
/// </summary>
public class AdStateModel
{
    public int CurrentRound { get; set; }
    public DateTimeOffset? RoundStartedAt { get; set; }
    public DateTimeOffset? RoundEndsAt { get; set; }
    public List<AdTeamServiceStateModel> Services { get; set; } = [];
}

public class AdTeamServiceStateModel
{
    public int AdTeamServiceId { get; set; }
    public int ChallengeId { get; set; }
    public string ChallengeTitle { get; set; } = string.Empty;
    public string? ContainerIp { get; set; }
    public int? ContainerPort { get; set; }

    /// <summary>The flag the team should currently be defending (their own).</summary>
    public string? CurrentFlag { get; set; }

    public string? LastCheckStatus { get; set; }
    public DateTimeOffset? LastResetAt { get; set; }
    public bool CanReset { get; set; }
    public int? ResetCooldownSecondsRemaining { get; set; }
}
