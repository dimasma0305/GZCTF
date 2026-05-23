using System.ComponentModel.DataAnnotations;
using GZCTF.Utils;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Models.Data;

/// <summary>
/// Outcome of one checker run against one team's service during one round. Used
/// to compute SLA score per (team, service) per round.
/// </summary>
[Index(nameof(AdTeamServiceId), nameof(AdRoundId))]
[Index(nameof(AdRoundId))]
public class AdCheckResult
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int AdTeamServiceId { get; set; }

    public AdTeamService AdTeamService { get; set; } = null!;

    [Required]
    public int AdRoundId { get; set; }

    public AdRound AdRound { get; set; } = null!;

    [Required]
    public AdCheckStatus Status { get; set; }

    /// <summary>Free-form error text from the checker on Mumble / Offline / InternalError. Null on Ok.</summary>
    [MaxLength(4096)]
    public string? ErrorMessage { get; set; }

    [Required]
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// IP the checker request egressed from. With the Phase 3 single-NAT
    /// design this is always the ad-net gateway IP — recorded for audit so
    /// the operator can confirm the NAT setup is honoured.
    /// </summary>
    [MaxLength(Limits.MaxIPLength)]
    public string? SourceIp { get; set; }
}
