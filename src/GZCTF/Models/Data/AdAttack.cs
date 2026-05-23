using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Models.Data;

/// <summary>
/// One successful attack submission. Created when an attacker team submits a flag
/// stolen from a victim team's service. The <c>(AttackerParticipationId, AdFlagId)</c>
/// uniqueness constraint prevents the same attacker from double-submitting the same
/// captured flag — but different attackers CAN submit the same flag (scaled by
/// 1/sqrt(N) per the FAUST formula).
/// </summary>
[Index(nameof(AttackerParticipationId), nameof(AdFlagId), IsUnique = true)]
[Index(nameof(VictimParticipationId))]
[Index(nameof(SubmittedAtRound))]
public class AdAttack
{
    [Key]
    public int Id { get; set; }

    /// <summary>The flag that was captured.</summary>
    [Required]
    public int AdFlagId { get; set; }

    public AdFlag AdFlag { get; set; } = null!;

    /// <summary>Attacker team's participation (the team that submitted).</summary>
    [Required]
    public int AttackerParticipationId { get; set; }

    public Participation AttackerParticipation { get; set; } = null!;

    /// <summary>
    /// Victim team's participation — denormalized from
    /// <c>AdFlag.AdTeamService.ParticipationId</c> for faster per-victim queries.
    /// </summary>
    [Required]
    public int VictimParticipationId { get; set; }

    public Participation VictimParticipation { get; set; } = null!;

    /// <summary>Challenge the captured flag belonged to. Always a
    /// <see cref="GameChallenge"/> — A&amp;D doesn't exist as an exercise type.</summary>
    [Required]
    public int ChallengeId { get; set; }

    public GameChallenge Challenge { get; set; } = null!;

    /// <summary>Round number at submission time. May be later than the round the flag was planted in (within the flag lifetime window).</summary>
    [Required]
    public int SubmittedAtRound { get; set; }

    [Required]
    public DateTimeOffset SubmittedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Points awarded to the attacker for this capture. Computed by the FAUST
    /// formula <c>base / sqrt(distinct_capturers)</c> — value here is the
    /// snapshot at submission time; the scoreboard renderer may recompute
    /// (e.g. if another team captures the same flag later, both attackers'
    /// effective points shrink).
    /// </summary>
    [Required]
    public double Points { get; set; }
}
