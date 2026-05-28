using System.ComponentModel.DataAnnotations;
using GZCTF.Utils;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Models.Data;

/// <summary>
/// Outcome of one King of the Hill tick for one hill: which team controlled it,
/// whether the service was functional, and the resulting score delta. One row per
/// (Challenge, Round). The scoreboard sums <see cref="HoldCredit"/> −
/// <see cref="Penalty"/> grouped by <see cref="ControllingParticipationId"/>.
///
/// <para><b>IsEnabled toggle semantics (D3):</b> historical KothControlResult rows
/// for a given KotH <see cref="GameChallenge"/> persist when an organizer toggles
/// <see cref="GameChallenge.IsEnabled"/> off; the rows are NOT cleared. Re-enabling
/// the same challenge resumes scoring against the existing history (so a hill
/// briefly disabled to fix a bug doesn't lose its accumulated leaderboard state).
/// If the organizer wants a clean slate they have to DELETE the GameChallenge
/// (cascade-removes results via the FK) — disabling is intentionally non-destructive.</para>
/// </summary>
[Index(nameof(ChallengeId), nameof(AdRoundId), IsUnique = true)]
[Index(nameof(AdRoundId))]
// Covers the per-game scoreboard aggregate query
// (AdScoreboardRepository.GenScoreboardAsync filters by GameId + ChallengeId
// then sums HoldCredit−Penalty per ControllingParticipationId). Without this
// the per-tick aggregate cost grows linearly with rounds×hills on long games.
[Index(nameof(GameId), nameof(ChallengeId))]
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
