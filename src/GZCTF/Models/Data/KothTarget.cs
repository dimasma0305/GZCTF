using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Models.Data;

/// <summary>
/// The single SHARED container for a King of the Hill challenge — one row per
/// (Game, Challenge) where the challenge is
/// <see cref="Utils.ChallengeType.KingOfTheHill"/>. Unlike <see cref="AdTeamService"/>
/// (one container per team), every team attacks this one box. Created + reconciled
/// by <c>AdContainerManager</c> and reset to its base image every
/// <c>Game.KothRefreshTicks</c> ticks.
/// </summary>
[Index(nameof(GameId), nameof(ChallengeId), IsUnique = true)]
public class KothTarget
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int GameId { get; set; }

    public Game Game { get; set; } = null!;

    [Required]
    public int ChallengeId { get; set; }

    public GameChallenge Challenge { get; set; } = null!;

    /// <summary>The shared container. Null briefly during a reset / refresh cycle.</summary>
    public Guid? ContainerId { get; set; }

    public Container? Container { get; set; }

    /// <summary>Round number at which the hill was last reset to its base image.</summary>
    public int LastRefreshRound { get; set; }
}
