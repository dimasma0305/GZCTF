using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Models.Data;

/// <summary>
/// A flag planted into ONE team's service container at ONE round. Stays valid for
/// <c>Challenge.AdFlagLifetimeTicks</c> ticks after <see cref="PlantedAtRound"/>;
/// attack submissions of an old flag past that window are rejected.
/// </summary>
[Index(nameof(AdTeamServiceId), nameof(PlantedAtRound))]
[Index(nameof(Flag))]
public class AdFlag
{
    [Key]
    public int Id { get; set; }

    /// <summary>Which team's service the flag was planted into.</summary>
    [Required]
    public int AdTeamServiceId { get; set; }

    public AdTeamService AdTeamService { get; set; } = null!;

    /// <summary>The round during which this flag was planted.</summary>
    [Required]
    public int AdRoundId { get; set; }

    public AdRound AdRound { get; set; } = null!;

    /// <summary>
    /// Round number copy (denormalized for lifetime-check queries — avoids a
    /// join when validating an attack submission).
    /// </summary>
    [Required]
    public int PlantedAtRound { get; set; }

    /// <summary>
    /// The flag string itself. Signed via the existing XorKey HMAC so the
    /// submission endpoint can verify validity without a DB lookup.
    /// </summary>
    [Required]
    [MaxLength(Limits.MaxFlagLength)]
    public string Flag { get; set; } = string.Empty;

    [Required]
    public DateTimeOffset PlantedAt { get; set; } = DateTimeOffset.UtcNow;
}
