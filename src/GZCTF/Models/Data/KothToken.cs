using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Models.Data;

/// <summary>
/// The rotating control token issued to ONE team for ONE refresh window — it is
/// GAME-WIDE, valid on EVERY King of the Hill hill in the game (not per-challenge).
/// The team writes this value into a hill's marker (<c>/koth/king</c>) to claim
/// control; the king-check reads the marker and matches it back to the issuing team,
/// regardless of which hill it was planted in. It rotates to a fresh value only on
/// the hill-reset boundary (every <c>Game.KothRefreshTicks</c> ticks), so write-once
/// camping (e.g. a <c>chattr +i</c>'d stale token) stops counting as control once the
/// window turns over. One token per team per window means a team plants the same
/// string on whichever hills it captures.
/// </summary>
[Index(nameof(ParticipationId), nameof(RoundNumber), IsUnique = true)]
// Unique on Token: 24 random bytes → 144 bits, collisions are astronomically
// unlikely but the marker-lookup uses FirstOrDefault, so a collision would
// otherwise silently arbitrate to whichever row sorted first. The unique
// constraint makes the lookup deterministic and lets the DB catch the
// (impossible-but-non-zero) collision instead of mis-attributing a controller.
// It also means a marker value pins exactly one team game-wide — which is what
// lets one token authenticate control on every hill.
[Index(nameof(Token), IsUnique = true)]
[Index(nameof(AdRoundId))]
public class KothToken
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int ParticipationId { get; set; }

    public Participation Participation { get; set; } = null!;

    /// <summary>
    /// Round number this token row is valid for (matches <see cref="AdRound.Number"/>).
    /// Exactly one row per (team, refresh window): it is minted at the window's ANCHOR
    /// round (every <c>Game.KothRefreshTicks</c> ticks, when the hill resets) and the
    /// king-check + token endpoint resolve a marker against that anchor, so the value
    /// stays stable for the whole window — a team plants once after a reset and holds.
    /// See <c>AdRoundService.AdvanceAsync</c>.
    /// </summary>
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
