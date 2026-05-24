using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Models.Data;

/// <summary>
/// Per-user-per-game API token for scripting A&amp;D flag submission. One row
/// per <c>(UserId, ParticipationId)</c> — every team member provisions their
/// own token so attribution + revocation are user-scoped. Member-kick
/// instantly invalidates that member's token; other team members keep theirs.
///
/// <para>Plaintext is shown exactly once at creation/rotation and never
/// persisted. Rotation updates the row in place (no IsRevoked flag — the
/// previous token cannot match the new hash).</para>
///
/// <para>Naming note: the class is still <c>AdTeamApiToken</c> for migration
/// compatibility (the DB table + the unique-constraint history). Conceptually
/// it's a per-user token scoped to a team-game pair.</para>
/// </summary>
[Index(nameof(UserId), nameof(ParticipationId), IsUnique = true)]
[Index(nameof(TokenHash))]
public class AdTeamApiToken
{
    [Key]
    public int Id { get; set; }

    /// <summary>The user this token belongs to. Auth resolves to this user.</summary>
    [Required]
    public Guid UserId { get; set; }

    public UserInfo User { get; set; } = null!;

    /// <summary>The participation (team + game) the token authorizes against.</summary>
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
    /// Public hint shown in the UI (e.g. <c>ad_a1b2…f9e8</c>) so the user
    /// can recognize which token is active without revealing it.
    /// </summary>
    [Required]
    [MaxLength(32)]
    public string Hint { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastRotatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastUsedAt { get; set; }
}
