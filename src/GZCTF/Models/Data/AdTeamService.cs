using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Models.Data;

/// <summary>
/// Persistent (team, A&amp;D challenge) container assignment. One row per (Participation,
/// Challenge) pair where the challenge is <see cref="Utils.ChallengeType.AttackDefense"/>.
/// Created by <c>AdContainerManager</c> on game start (or on participation accept for
/// late joiners). The associated <see cref="Container"/> lives for the entire game.
/// </summary>
[Index(nameof(ParticipationId), nameof(ChallengeId), IsUnique = true)]
public class AdTeamService
{
    [Key]
    public int Id { get; set; }

    /// <summary>Team's participation in the game.</summary>
    [Required]
    public int ParticipationId { get; set; }

    public Participation Participation { get; set; } = null!;

    /// <summary>The A&amp;D challenge this container hosts. Always a
    /// <see cref="GameChallenge"/> — A&amp;D doesn't exist as an exercise type.</summary>
    [Required]
    public int ChallengeId { get; set; }

    public GameChallenge Challenge { get; set; } = null!;

    /// <summary>
    /// Underlying container. Nullable — set after successful launch, may be
    /// null briefly during a reset / recreate cycle.
    /// </summary>
    public Guid? ContainerId { get; set; }

    public Container? Container { get; set; }

    /// <summary>
    /// When this team last self-reset the container. Null if they've never
    /// reset. Used by the cooldown gate on the reset endpoint.
    /// </summary>
    public DateTimeOffset? LastResetAt { get; set; }

    /// <summary>
    /// Blob storage key for the post-game snapshot tarball (<c>docker save</c>
    /// gzipped). Set at game end if the game's <c>AdAllowSnapshotDownload</c>
    /// is true. Null otherwise.
    /// </summary>
    [MaxLength(256)]
    public string? SnapshotBlobKey { get; set; }
}
