using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Models.Data;

/// <summary>
/// The rotating control token issued to ONE team for ONE King of the Hill challenge
/// at ONE round. The team writes this value into the hill's marker (<c>/koth/king</c>)
/// to claim control for the tick; the king-check reads the marker and matches it back
/// to the issuing team. It rotates every tick, so write-once camping (e.g. a
/// <c>chattr +i</c>'d stale token) no longer counts as control.
/// </summary>
[Index(nameof(ParticipationId), nameof(ChallengeId), nameof(RoundNumber), IsUnique = true)]
[Index(nameof(Token))]
public class KothToken
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int ParticipationId { get; set; }

    public Participation Participation { get; set; } = null!;

    [Required]
    public int ChallengeId { get; set; }

    public GameChallenge Challenge { get; set; } = null!;

    /// <summary>Round number this token is valid for (matches <see cref="AdRound.Number"/>).</summary>
    [Required]
    public int RoundNumber { get; set; }

    /// <summary>The unguessable token string the team plants into the marker.</summary>
    [Required]
    [MaxLength(Limits.MaxFlagLength)]
    public string Token { get; set; } = string.Empty;

    [Required]
    public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;
}
