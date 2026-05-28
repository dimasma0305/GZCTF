using System.ComponentModel.DataAnnotations;
using GZCTF.Utils;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Models.Data;

/// <summary>
/// Outcome of one King of the Hill tick for one hill: which team controlled it,
/// whether the service was functional, and the resulting score delta. One row per
/// (Challenge, Round). The scoreboard sums <see cref="HoldCredit"/> −
/// <see cref="Penalty"/> grouped by <see cref="ControllingParticipationId"/>.
/// </summary>
[Index(nameof(ChallengeId), nameof(AdRoundId), IsUnique = true)]
[Index(nameof(AdRoundId))]
public class KothControlResult
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int GameId { get; set; }

    [Required]
    public int ChallengeId { get; set; }

    public GameChallenge Challenge { get; set; } = null!;

    [Required]
    public int AdRoundId { get; set; }

    public AdRound AdRound { get; set; } = null!;

    /// <summary>
    /// Team controlling the hill this tick (their current token sat in the marker).
    /// Null = no valid king this tick → nobody scores.
    /// </summary>
    public int? ControllingParticipationId { get; set; }

    public Participation? ControllingParticipation { get; set; }

    /// <summary>Functional-probe verdict for the hill this tick (reuses the A&amp;D checker).</summary>
    [Required]
    public AdCheckStatus Status { get; set; }

    /// <summary>Hold points credited to the controller this tick (0 when no king or the hill is broken).</summary>
    public double HoldCredit { get; set; }

    /// <summary>
    /// Penalty debited from the controller this tick — the flat −1 when a team holds
    /// a Mumble/Offline hill; 0 otherwise.
    /// </summary>
    public double Penalty { get; set; }

    [MaxLength(4096)]
    public string? ErrorMessage { get; set; }

    [Required]
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
}
