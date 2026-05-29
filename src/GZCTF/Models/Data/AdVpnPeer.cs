using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Models.Data;

/// <summary>
/// Per-member WireGuard peer for an A&amp;D game. One row per (UserInfo, Participation)
/// so removing a single team member invalidates only their peer (the rest of the team
/// stays connected). Generated server-side on participation accept + on subsequent
/// team-member-add events for active A&amp;D games.
/// </summary>
[Index(nameof(UserId), nameof(ParticipationId), IsUnique = true)]
// AssignedIp is unique GLOBALLY, not per game: every peer across all games renders onto
// the single shared wg0 interface, so two games sharing a /32 would collide there. A
// per-(GameId, AssignedIp) index let concurrent cross-game provisioning hand the same /32
// to two teams; the global unique index makes the second insert fail instead (the caller
// re-allocates the next free address).
[Index(nameof(AssignedIp), IsUnique = true)]
public class AdVpnPeer
{
    [Key]
    public int Id { get; set; }

    [Required]
    public Guid UserId { get; set; }

    public UserInfo User { get; set; } = null!;

    [Required]
    public int ParticipationId { get; set; }

    public Participation Participation { get; set; } = null!;

    /// <summary>
    /// Denormalized game id (== Participation.GameId). Used for per-game queries and
    /// WireGuard rendering. NOTE: <see cref="AssignedIp"/> is unique GLOBALLY (see the
    /// class-level index), not per game — all peers share one wg0 interface.
    /// </summary>
    [Required]
    public int GameId { get; set; }

    /// <summary>WireGuard public key — programmed into the WG server config.</summary>
    [Required]
    [MaxLength(64)]
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>
    /// WireGuard private key, XOR-obfuscated at rest. Returned to the user via
    /// authenticated download as part of their .conf file (one-shot — UI warns).
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// Per-member /32 assigned from the VPN client subnet, e.g. "10.13.37.5".
    /// Unique GLOBALLY (all peers share one wg0 interface — see the class index).
    /// </summary>
    [Required]
    [MaxLength(Limits.MaxIPLength)]
    public string AssignedIp { get; set; } = string.Empty;

    /// <summary>
    /// Set when the peer is revoked (member kicked, admin force-revoke).
    /// Revoked peers are removed from the running WG config via
    /// <c>wg set &lt;iface&gt; peer &lt;pubkey&gt; remove</c>.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
