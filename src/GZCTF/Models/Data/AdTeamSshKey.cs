using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Models.Data;

/// <summary>
/// Per-member SSH keypair for A&amp;D container access. One row per (UserInfo,
/// Participation). Each team member has their own key — clean revocation when a
/// member is removed mid-event (only their key invalidates; rest of the team stays
/// connected).
/// </summary>
[Index(nameof(UserId), nameof(ParticipationId), IsUnique = true)]
public class AdTeamSshKey
{
    [Key]
    public int Id { get; set; }

    /// <summary>Which user this key belongs to.</summary>
    [Required]
    public Guid UserId { get; set; }

    public UserInfo User { get; set; } = null!;

    /// <summary>The participation this key is scoped to (team + game).</summary>
    [Required]
    public int ParticipationId { get; set; }

    public Participation Participation { get; set; } = null!;

    /// <summary>SSH public key — injected into team containers' authorized_keys.</summary>
    [Required]
    [MaxLength(8192)]
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>
    /// SSH private key, XOR-obfuscated at rest via <c>IConfigService.GetXorKey()</c>
    /// (same shape as registry passwords / repo-binding tokens). Returned to the
    /// user via authenticated download.
    /// </summary>
    [Required]
    [MaxLength(8192)]
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>Algorithm — ed25519 by default.</summary>
    [Required]
    [MaxLength(32)]
    public string Algorithm { get; set; } = "ed25519";

    /// <summary>
    /// Set when the key is revoked (member kicked from team, or admin force-revoke).
    /// Revoked keys are stripped from authorized_keys on next container reload.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
