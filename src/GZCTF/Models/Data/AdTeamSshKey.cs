using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Models.Data;

/// <summary>
/// Per-member SSH key for A&amp;D shell access via the jump-host sidecar.
/// One row per (UserInfo, Participation). Each team member has their own
/// key — clean revocation when a member is removed mid-event (only their
/// key invalidates; rest of the team stays connected).
///
/// <para>The jump host's <c>AuthorizedKeysCommand</c> resolves
/// <c>(Fingerprint, ChallengeId-from-ssh-username)</c> → this row → which
/// container to <c>docker exec</c>. The pubkey itself never leaves the DB;
/// only its fingerprint is indexed for lookup, so the jump host can
/// authenticate without the platform having to dump every key on every
/// connection.</para>
/// </summary>
[Index(nameof(UserId), nameof(ParticipationId), IsUnique = true)]
[Index(nameof(Fingerprint))]
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

    /// <summary>SSH public key in OpenSSH single-line format (<c>ssh-ed25519 AAAA... [comment]</c>).</summary>
    [Required]
    [MaxLength(8192)]
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>
    /// SSH private key when the platform generated the keypair on the
    /// user's behalf, XOR-obfuscated at rest via
    /// <c>IConfigService.GetXorKey()</c>. NULL when the user uploaded
    /// their own public key (the preferred path — private half never
    /// touches the server).
    /// </summary>
    [MaxLength(8192)]
    public string? PrivateKey { get; set; }

    /// <summary>
    /// SHA256 fingerprint in OpenSSH-canonical <c>SHA256:&lt;base64&gt;</c>
    /// shape (matches <c>ssh-keygen -lf</c>). Indexed so the jump host's
    /// <c>AuthorizedKeysCommand</c> can resolve presented keys in O(1)
    /// without scanning every row.
    /// </summary>
    [Required]
    [MaxLength(80)]
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>Algorithm — ed25519 by default.</summary>
    [Required]
    [MaxLength(32)]
    public string Algorithm { get; set; } = "ssh-ed25519";

    /// <summary>True iff the platform generated this keypair (PrivateKey holds the ciphertext).</summary>
    public bool PlatformGenerated { get; set; }

    /// <summary>
    /// Set when the key is revoked (member kicked from team, or user
    /// force-revoke). Revoked keys are excluded from the jump-host
    /// lookup on the next connection — no per-connection container
    /// poke needed.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastUsedAt { get; set; }
}
