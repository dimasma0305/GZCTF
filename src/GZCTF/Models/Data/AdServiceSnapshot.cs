using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Models.Data;

/// <summary>
/// A point-in-time capture of a team service's changed-file manifest during a
/// running game, taken by <c>AdSnapshotService</c>. Stored deduped — a new row
/// is written only when the changed-file set differs from the service's previous
/// snapshot — so an admin can diff a service between two points in time (which
/// files the team touched between round X and round Y). File-level only; the
/// content of any file is read live (current vs baseline) on demand.
/// </summary>
[Index(nameof(AdTeamServiceId), nameof(AdRoundId))]
public class AdServiceSnapshot
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int AdTeamServiceId { get; set; }

    public AdTeamService AdTeamService { get; set; } = null!;

    /// <summary>The round during which this manifest was captured.</summary>
    [Required]
    public int AdRoundId { get; set; }

    public AdRound AdRound { get; set; } = null!;

    [Required]
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// JSON array of the changed files at capture time — same shape as
    /// <c>AdTeamService.SnapshotChanges</c>: <c>[{ "p": path, "k": kind }]</c>
    /// (kind 0 = modified, 1 = added, 2 = deleted; K8s is mtime-based so all 0).
    /// </summary>
    public string ManifestJson { get; set; } = "[]";
}
