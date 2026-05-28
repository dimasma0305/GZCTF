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
// Unique on Token: 24 random bytes → 144 bits, collisions are astronomically
// unlikely but the marker-lookup uses FirstOrDefault, so a collision would
// otherwise silently arbitrate to whichever row sorted first. The unique
// constraint makes the lookup deterministic and lets the DB catch the
// (impossible-but-non-zero) collision instead of mis-attributing a controller.
[Index(nameof(Token), IsUnique = true)]
[Index(nameof(AdRoundId))]
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

    /// <summary>
    /// FK back to <see cref="AdRound"/>. Without this, a manually-deleted round
    /// leaves orphan KothToken rows that accumulate forever (the previous
    /// schema only stored RoundNumber as a bare int with no constraint). Cascade
    /// delete so dropping a round cleans up its tokens atomically.
    /// </summary>
    [Required]
    public int AdRoundId { get; set; }

    public AdRound AdRound { get; set; } = null!;

    /// <summary>The unguessable token string the team plants into the marker.</summary>
    [Required]
    [MaxLength(Limits.MaxFlagLength)]
    public string Token { get; set; } = string.Empty;

    [Required]
    public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;
}
