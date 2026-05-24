using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Models.Data;

/// <summary>
/// Per-team API token for scripting A&amp;D flag submission. One row per
/// <see cref="Participation"/> — when a player rotates the token, the row is
/// updated in place (no IsRevoked flag — the previous token cannot match the
/// new hash). Plaintext is shown to the captain once at creation/rotation and
/// never persisted.
/// </summary>
[Index(nameof(ParticipationId), IsUnique = true)]
[Index(nameof(TokenHash))]
public class AdTeamApiToken
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int ParticipationId { get; set; }

    public Participation Participation { get; set; } = null!;

    /// <summary>
    /// HMAC-SHA256 of the plaintext token, keyed with
    /// <c>IConfigService.GetXorKey()</c>. Storing a one-way hash means a DB
    /// leak does not yield usable tokens; rotation invalidates the previous
    /// token unconditionally.
    /// </summary>
    [Required]
    [MaxLength(64)]
    public byte[] TokenHash { get; set; } = [];

    /// <summary>
    /// Public hint shown in the UI (e.g. <c>ad_a1b2…f9e8</c>) so the captain
    /// can recognize which token is active without revealing it.
    /// </summary>
    [Required]
    [MaxLength(32)]
    public string Hint { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastRotatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastUsedAt { get; set; }
}
