using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GZCTF.Models.Request.Edit;

namespace GZCTF.Models.Data;

public class GameChallenge : Challenge
{
    /// <summary>
    /// Whether to record traffic
    /// </summary>
    public bool EnableTrafficCapture { get; set; }

    /// <summary>
    /// Whether to disable blood bonus
    /// </summary>
    public bool DisableBloodBonus { get; set; }

    /// <summary>
    /// Initial score
    /// </summary>
    [Required]
    public int OriginalScore { get; set; } = 1000;

    /// <summary>
    /// Minimum score rate
    /// </summary>
    [Required]
    [Range(0, 1)]
    public double MinScoreRate { get; set; } = 0.25;

    /// <summary>
    /// Difficulty coefficient
    /// </summary>
    [Required]
    public double Difficulty { get; set; } = 5;

    /// <summary>
    /// Current score of the challenge
    /// </summary>
    [NotMapped]
    public int CurrentScore => CalculateChallengeScore(
        OriginalScore,
        MinScoreRate,
        Difficulty,
        FirstSolves?.Count ?? 0);


    internal static int CalculateChallengeScore(int originalScore, double minScoreRate, double difficulty,
        int acceptedCount)
    {
        if (acceptedCount <= 1)
            return originalScore;

        return (int)Math.Floor(
            originalScore *
            (minScoreRate + (1.0 - minScoreRate) * Math.Exp((1 - acceptedCount) / difficulty)));
    }

    internal void Update(ChallengeUpdateModel model)
    {
        Title = model.Title ?? Title;
        Content = model.Content ?? Content;
        Category = model.Category ?? Category;
        Hints = model.Hints ?? Hints;
        CPUCount = model.CPUCount ?? CPUCount;
        MemoryLimit = model.MemoryLimit ?? MemoryLimit;
        StorageLimit = model.StorageLimit ?? StorageLimit;
        ContainerImage = model.ContainerImage?.Trim() ?? ContainerImage;
        ExposePort = model.ExposePort ?? ExposePort;
        NetworkMode = model.NetworkMode ?? NetworkMode;
        OriginalScore = model.OriginalScore ?? OriginalScore;
        MinScoreRate = model.MinScoreRate ?? MinScoreRate;
        Difficulty = model.Difficulty ?? Difficulty;
        FileName = model.FileName ?? FileName;
        DisableBloodBonus = model.DisableBloodBonus ?? DisableBloodBonus;
        SubmissionLimit = model.SubmissionLimit ?? SubmissionLimit;

        // isEnabled should be updated alone
        IsEnabled = model.IsEnabled ?? IsEnabled;

        // only set DeadlineUtc to null when pass DateTimeOffset.MinValue (but not null)
        if (model.DeadlineUtc is { } time)
            DeadlineUtc = time.ToUnixTimeSeconds() == 0 ? null : time;

        // only set FlagTemplate to null when pass an empty string (but not null)
        if (model.FlagTemplate is { } template)
            FlagTemplate = string.IsNullOrWhiteSpace(template) ? null : template;

        // Container only
        EnableTrafficCapture = Type.IsContainer() && (model.EnableTrafficCapture ?? EnableTrafficCapture);
    }

    #region Db Relationship

    /// <summary>
    /// Submissions
    /// </summary>
    public List<Submission> Submissions { get; set; } = [];

    /// <summary>
    /// Challenge instances
    /// </summary>
    public List<GameInstance> Instances { get; set; } = [];

    /// <summary>
    /// Teams that activated the challenge
    /// </summary>
    public HashSet<Participation> Teams { get; set; } = [];

    /// <summary>
    /// Configurations for divisions
    /// </summary>
    public HashSet<DivisionChallengeConfig> DivisionConfigs { get; set; } = [];

    /// <summary>
    /// First solves recorded for this challenge.
    /// </summary>
    public List<FirstSolve>? FirstSolves { get; set; } = [];

    /// <summary>
    /// Game ID
    /// </summary>
    public int GameId { get; set; }

    /// <summary>
    /// Game object
    /// </summary>
    public Game Game { get; set; } = null!;

    #endregion Db Relationship

    #region Attack & Defense fields
    // All Ad* fields are nullable + only consulted when Type == ChallengeType.AttackDefense.
    // Existing container fields (inherited from Challenge: ContainerImage, ExposePort,
    // CPUCount, MemoryLimit, StorageLimit) are reused for A&D — no duplication.

    /// <summary>
    /// Docker image for the per-challenge checker container. Must speak the
    /// enochecker3 HTTP contract (PUT /putflag / /getflag / /havoc).
    /// </summary>
    public string? AdCheckerImage { get; set; }

    /// <summary>
    /// Seconds per tick. The checker runs once per (team, service) per tick;
    /// flags rotate at tick boundaries. Industry norm is 60–180s; default 120.
    /// </summary>
    public int? AdTickSeconds { get; set; } = 120;

    /// <summary>
    /// Number of ticks a planted flag remains valid for attack submission.
    /// Default 5 = an attacker has 5 ticks to exfiltrate before the flag rotates out.
    /// </summary>
    public int? AdFlagLifetimeTicks { get; set; } = 5;

    /// <summary>
    /// If true, team containers can reach the public internet. Default false
    /// (sandboxed). Opt-in per challenge for services that genuinely need an
    /// external API call.
    /// </summary>
    public bool AdAllowEgress { get; set; }

    /// <summary>
    /// If true, teams can self-reset their own container to the baseline image
    /// (subject to <see cref="AdResetCooldownMinutes"/>). Default true — lets
    /// teams recover from being fully owned without operator intervention.
    /// </summary>
    public bool AdAllowSelfReset { get; set; } = true;

    /// <summary>
    /// Minimum minutes between consecutive self-resets of the same team's
    /// container. Default 5. Prevents reset spam.
    /// </summary>
    public int? AdResetCooldownMinutes { get; set; } = 5;

    /// <summary>
    /// If true, each team's final container state is committed + saved as a
    /// gzipped tarball at game end and made available for download. Default
    /// true — friendly for educational events / post-mortems.
    /// </summary>
    public bool AdAllowSnapshotDownload { get; set; } = true;

    /// <summary>
    /// Putflag jitter window as a fraction of the tick duration. Default 0.4
    /// = the checker plants flags somewhere in the first 40% of each tick.
    /// Per-(team, service) random offset within this window — defeats timing-
    /// based Superman patches.
    /// </summary>
    public double? AdPutflagWindowFraction { get; set; } = 0.4;

    /// <summary>
    /// Getflag jitter window as a fraction of the tick duration, applied
    /// after putflag + min grace period. Default 0.5.
    /// </summary>
    public double? AdGetflagWindowFraction { get; set; } = 0.5;

    /// <summary>
    /// Seconds after putflag before getflag may fire — gives the service time
    /// to commit the flag. Default 3.
    /// </summary>
    public int? AdMinGracePeriodSeconds { get; set; } = 3;

    #endregion
}
