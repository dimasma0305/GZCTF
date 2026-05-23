using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Models.Data;

/// <summary>
/// A single A&amp;D tick. One row per round per game. Created at tick boundaries by
/// <c>AdCheckerScheduler</c> (Phase 2) or by an admin via the manual advance-round
/// endpoint (Phase 1). Per-team-per-service flags + check results pivot off this.
/// </summary>
[Index(nameof(GameId), nameof(Number), IsUnique = true)]
public class AdRound
{
    [Key]
    public int Id { get; set; }

    /// <summary>Game this round belongs to.</summary>
    [Required]
    public int GameId { get; set; }

    public Game Game { get; set; } = null!;

    /// <summary>1-indexed round number within the game.</summary>
    [Required]
    public int Number { get; set; }

    [Required]
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Tick deadline — set when the round starts as <c>StartedAt + AdTickSeconds</c>
    /// for the shortest tick across active A&amp;D challenges. Scoring for this round
    /// settles after this timestamp.
    /// </summary>
    [Required]
    public DateTimeOffset EndsAt { get; set; }

    /// <summary>
    /// Set when scoring has been computed and persisted for this round. Idempotent —
    /// scheduler won't double-count if it crashes mid-round and restarts.
    /// </summary>
    public DateTimeOffset? ScoredAt { get; set; }
}
