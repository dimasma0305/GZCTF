using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace GZCTF.Models.Request.Edit;

/// <summary>
/// Game information (Edit)
/// </summary>
public class GameInfoModel : IValidatableObject
{
    /// <summary>
    /// Game ID
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Game title
    /// </summary>
    [Required]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Is hidden
    /// </summary>
    public bool Hidden { get; set; }

    /// <summary>
    /// Game summary
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Game detailed description
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Accept teams without review
    /// </summary>
    public bool AcceptWithoutReview { get; set; }

    /// <summary>
    /// Whether users may submit challenges for this game (with admin review).
    /// Default false — admin opts a game in to community submissions.
    /// </summary>
    public bool AllowUserSubmissions { get; set; } = false;

    /// <summary>
    /// Is writeup required
    /// </summary>
    public bool WriteupRequired { get; set; }

    /// <summary>
    /// Game invitation code
    /// </summary>
    [MaxLength(Limits.InviteTokenLength,
        ErrorMessageResourceName = nameof(Resources.Program.Model_InvitationCodeTooLong),
        ErrorMessageResourceType = typeof(Resources.Program))]
    public string? InviteCode { get; set; }

    /// <summary>
    /// Team member count limit, 0 means no limit
    /// </summary>
    public int TeamMemberCountLimit { get; set; }

    /// <summary>
    /// Container count limit per team
    /// </summary>
    public int ContainerCountLimit { get; set; } = 3;

    /// <summary>
    /// Discord webhook URL
    /// </summary>
    [MaxLength(Limits.UrlLength)]
    [JsonPropertyName("discordWebhook")]
    public string? DiscordWebhook { get; set; }

    /// <summary>
    /// Game poster URL
    /// </summary>
    [JsonPropertyName("poster")]
    public string? PosterUrl { get; set; } = string.Empty;

    /// <summary>
    /// Game public key
    /// </summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Is the game in practice mode (accessible even after the game ends)
    /// </summary>
    public bool PracticeMode { get; set; } = true;

    /// <summary>
    /// Start time
    /// </summary>
    [Required]
    [JsonPropertyName("start")]
    public DateTimeOffset StartTimeUtc { get; set; } = DateTimeOffset.FromUnixTimeSeconds(0);

    /// <summary>
    /// End time
    /// </summary>
    [Required]
    [JsonPropertyName("end")]
    public DateTimeOffset EndTimeUtc { get; set; } = DateTimeOffset.FromUnixTimeSeconds(0);

    /// <summary>
    /// Optional scoreboard freeze time. If set, must fall strictly between StartTimeUtc and EndTimeUtc.
    /// Non-monitor viewers see a snapshot built at this time; admins/monitors always see live.
    /// </summary>
    [JsonPropertyName("freeze")]
    public DateTimeOffset? FreezeTimeUtc { get; set; }

    /// <summary>
    /// Writeup submission deadline
    /// </summary>
    public DateTimeOffset WriteupDeadline { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Writeup additional notes
    /// </summary>
    public string WriteupNote { get; set; } = string.Empty;

    /// <summary>
    /// Blood bonus points
    /// </summary>
    [JsonPropertyName("bloodBonus")]
    public long BloodBonusValue { get; set; } = BloodBonus.DefaultValue;

    /// <summary>
    /// A&amp;D — warmup seconds before round 1 starts (default 1800 = 30 min).
    /// Teams get this gap to SSH in + write initial patches before scoring.
    /// Only consulted by games containing AttackDefense challenges.
    /// </summary>
    public int? AdWarmupSeconds { get; set; }

    /// <summary>
    /// A&amp;D — how long to retain per-team container snapshots after game end.
    /// </summary>
    public int? AdSnapshotRetentionDays { get; set; }

    /// <summary>
    /// A&amp;D — seconds per tick (the global scoring unit). Every A&amp;D service
    /// in the game shares one tick. Default 120.
    /// </summary>
    public int? AdTickSeconds { get; set; }

    /// <summary>
    /// A&amp;D — how many ticks a planted flag stays valid for attack submission
    /// (the uniform attack window). Default 5.
    /// </summary>
    public int? AdFlagLifetimeTicks { get; set; }

    /// <summary>
    /// A&amp;D — minimum minutes between a team's self-resets (anti-spam). Default 5.
    /// </summary>
    public int? AdResetCooldownMinutes { get; set; }

    /// <summary>
    /// A&amp;D — whether team containers are snapshotted at game end for download.
    /// Default true.
    /// </summary>
    public bool? AdAllowSnapshotDownload { get; set; }

    /// <summary>
    /// A&amp;D — getflag jitter window as a fraction of the tick (default 0.5).
    /// Event-wide; the random offset is still rolled per (team, service, round).
    /// </summary>
    public double? AdGetflagWindowFraction { get; set; }

    /// <summary>
    /// A&amp;D — seconds after a round starts before getflag may fire (default 3).
    /// </summary>
    public int? AdMinGracePeriodSeconds { get; set; }

    internal static GameInfoModel FromGame(Data.Game game) =>
        new()
        {
            Id = game.Id,
            Title = game.Title,
            Summary = game.Summary,
            Content = game.Content,
            Hidden = game.Hidden,
            PracticeMode = game.PracticeMode,
            PosterUrl = game.PosterUrl,
            InviteCode = game.InviteCode,
            PublicKey = game.PublicKey,
            AcceptWithoutReview = game.AcceptWithoutReview,
            AllowUserSubmissions = game.AllowUserSubmissions,
            TeamMemberCountLimit = game.TeamMemberCountLimit,
            ContainerCountLimit = game.ContainerCountLimit,
            DiscordWebhook = game.DiscordWebhook,
            StartTimeUtc = game.StartTimeUtc,
            EndTimeUtc = game.EndTimeUtc,
            FreezeTimeUtc = game.FreezeTimeUtc,
            WriteupDeadline = game.WriteupDeadline,
            WriteupNote = game.WriteupNote,
            WriteupRequired = game.WriteupRequired,
            BloodBonusValue = game.BloodBonus.Val,
            AdWarmupSeconds = game.AdWarmupSeconds,
            AdSnapshotRetentionDays = game.AdSnapshotRetentionDays,
            AdTickSeconds = game.AdTickSeconds,
            AdFlagLifetimeTicks = game.AdFlagLifetimeTicks,
            AdResetCooldownMinutes = game.AdResetCooldownMinutes,
            AdAllowSnapshotDownload = game.AdAllowSnapshotDownload,
            AdGetflagWindowFraction = game.AdGetflagWindowFraction,
            AdMinGracePeriodSeconds = game.AdMinGracePeriodSeconds
        };

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (FreezeTimeUtc is { } freeze &&
            (freeze <= StartTimeUtc || freeze >= EndTimeUtc))
        {
            yield return new ValidationResult(
                "FreezeTimeUtc must be strictly between StartTimeUtc and EndTimeUtc.",
                [nameof(FreezeTimeUtc)]);
        }
    }
}
