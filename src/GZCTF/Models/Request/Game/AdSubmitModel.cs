using System.ComponentModel.DataAnnotations;

namespace GZCTF.Models.Request.Game;

/// <summary>
/// Body for POST /api/game/{id}/ad/submit — batch attack submission. Exploit
/// scripts typically capture many flags per tick and submit them together.
/// Bounded at <see cref="MaxFlagsPerBatch"/> to keep request bodies small and
/// limit scoring work per request.
/// </summary>
public class AdBatchSubmitModel
{
    public const int MaxFlagsPerBatch = 100;

    /// <summary>Flag strings captured from other teams' services.</summary>
    [Required]
    [MinLength(1)]
    [MaxLength(MaxFlagsPerBatch)]
    public List<string> Flags { get; set; } = [];
}

/// <summary>
/// Per-flag result of an attack submission. Returned in input order so the
/// caller can correlate results with their submitted flags.
/// </summary>
public class AdSubmitResultModel
{
    /// <summary>The flag string this result corresponds to (echoed for correlation).</summary>
    public string Flag { get; set; } = string.Empty;

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
/// Response for the batch submit endpoint. <see cref="Results"/> is ordered to
/// match the submitted <see cref="AdBatchSubmitModel.Flags"/>.
/// </summary>
public class AdBatchSubmitResultModel
{
    /// <summary>Number of submissions accepted (sums points across them).</summary>
    public int AcceptedCount { get; set; }

    /// <summary>Sum of points awarded across all accepted submissions in this batch.</summary>
    public double TotalPoints { get; set; }

    /// <summary>Per-flag result, in submission order.</summary>
    public List<AdSubmitResultModel> Results { get; set; } = [];
}

/// <summary>
/// Response from generating/rotating a team API token. The plaintext token is
/// returned exactly once; subsequent reads only return the hint.
/// </summary>
public class AdTokenGenerateResultModel
{
    /// <summary>Plaintext token. Show once to the captain, never persist client-side.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Stable public hint (e.g. <c>ad_a1b2…f9e8</c>).</summary>
    public string Hint { get; set; } = string.Empty;

    public DateTimeOffset RotatedAt { get; set; }
}

/// <summary>
/// Response for the hint endpoint — visible to all team members.
/// </summary>
public class AdTokenHintModel
{
    /// <summary>True iff a token exists for this team in this game.</summary>
    public bool Exists { get; set; }

    /// <summary>Public hint (e.g. <c>ad_a1b2…f9e8</c>); empty when Exists=false.</summary>
    public string Hint { get; set; } = string.Empty;

    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? LastRotatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>True iff the caller is the captain of the participating team (can rotate/revoke).</summary>
    public bool CanManage { get; set; }
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
