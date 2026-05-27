using YamlDotNet.Serialization;

namespace GZCTF.Models.Request.Edit;

/// <summary>
/// In-memory shape of a <c>.gzevent</c> manifest file parsed by
/// <see cref="GZCTF.Services.Transfer.RepoBindingDiscoveryService"/>.
/// Mirrors the schema gzcli publishes at
/// <c>https://raw.githubusercontent.com/dimasma0305/gzcli/refs/heads/master/internal/template/templates/others/ctf-template/.gzctf/gzevent.schema.yaml</c>.
///
/// Field names use camelCase to match the published schema; YamlDotNet
/// is configured with <see cref="YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention"/>
/// to bind them.
/// </summary>
public sealed class GzEventModel
{
    public string? Title { get; set; }

    public DateTimeOffset? Start { get; set; }

    public DateTimeOffset? End { get; set; }

    /// <summary>Repo-relative path to a poster image; not applied in v1.</summary>
    public string? Poster { get; set; }

    public bool? Hidden { get; set; }

    public string? Summary { get; set; }

    public string? Content { get; set; }

    public bool? AcceptWithoutReview { get; set; }

    public string? InviteCode { get; set; }

    public List<string>? Organizations { get; set; }

    public int? TeamMemberCountLimit { get; set; }

    public int? ContainerCountLimit { get; set; }

    public bool? PracticeMode { get; set; }

    public bool? WriteupRequired { get; set; }

    public DateTimeOffset? WriteupDeadline { get; set; }

    public string? WriteupNote { get; set; }

    public long? BloodBonus { get; set; }

    /// <summary>
    /// Event-wide Attack &amp; Defense settings, applied to the Game. These are
    /// shared by every A&amp;D challenge in the event (rounds span the whole game,
    /// so the tick — and the checker timing knobs that are fractions of it — are
    /// per-game, not per-challenge). All optional — omitted ones keep the platform
    /// defaults. The per-challenge knobs (checker image, egress, self-reset) live
    /// in each challenge's <c>ad:</c> block.
    /// </summary>
    public AdEventSection? Ad { get; set; }

    public sealed class AdEventSection
    {
        /// <summary>Round/tick length in seconds. Default 60.</summary>
        public int? TickSeconds { get; set; }

        /// <summary>How many ticks a planted flag stays submittable. Default 5.</summary>
        public int? FlagLifetimeTicks { get; set; }

        /// <summary>Grace seconds after game start before scoring begins. Default 1800.</summary>
        public int? WarmupSeconds { get; set; }

        /// <summary>Minutes a team must wait between self-resets. Default 5.</summary>
        public int? ResetCooldownMinutes { get; set; }

        /// <summary>Whether teams may download the post-game container snapshot. Default true.</summary>
        public bool? AllowSnapshotDownload { get; set; }

        /// <summary>Days to retain post-game snapshots; null = keep indefinitely.</summary>
        public int? SnapshotRetentionDays { get; set; }

        /// <summary>Getflag jitter window as a fraction of the tick. Default 0.5.</summary>
        public double? GetflagWindowFraction { get; set; }

        /// <summary>Seconds after a round starts before getflag may fire. Default 3.</summary>
        public int? MinGracePeriodSeconds { get; set; }
    }
}
