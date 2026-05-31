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

        // Attack & Defense per-challenge knobs
        AdCheckerImage = model.AdCheckerImage?.Trim() ?? AdCheckerImage;
        AdAllowEgress = model.AdAllowEgress ?? AdAllowEgress;
        AdAllowSelfReset = model.AdAllowSelfReset ?? AdAllowSelfReset;
        AdSshRequiresFlag = model.AdSshRequiresFlag ?? AdSshRequiresFlag;

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
    /// If true, team containers can reach the public internet. Default true
    /// (open) — most A&D services expect outbound access. Set false per
    /// challenge to sandbox a service that should have no egress.
    /// </summary>
    public bool AdAllowEgress { get; set; } = true;

    /// <summary>
    /// If true, teams can self-reset their own container to the baseline image
    /// (subject to the game-wide <see cref="Game.AdResetCooldownMinutes"/>).
    /// Default true — lets teams recover from being fully owned without
    /// operator intervention. Per-challenge because some fragile services
    /// shouldn't be resettable at all.
    /// </summary>
    public bool AdAllowSelfReset { get; set; } = true;

    /// <summary>
    /// A&D only: when true, a team can SSH into its service container only after it
    /// has submitted at least one accepted captured flag (captured an opponent's
    /// flag) for this challenge. Enforced in InternalAdSshController.Lookup (the
    /// ssh-jump authorize path). Default false — SSH open to all keyholders.
    /// </summary>
    public bool AdSshRequiresFlag { get; set; }

    // Tick length, flag lifetime, reset cooldown, snapshot-download, and the
    // checker timing knobs (getflag jitter window + min grace period) are all
    // EVENT-WIDE policy and live on Game (AdTickSeconds, AdFlagLifetimeTicks,
    // AdResetCooldownMinutes, AdAllowSnapshotDownload, AdGetflagWindowFraction,
    // AdMinGracePeriodSeconds). Rounds span the whole game, so a per-challenge
    // tick was never actually honored, and the jitter/grace are fractions of
    // that shared tick — the random offset is still rolled per (team, service,
    // round), so anti-fingerprinting is unaffected by sharing one window size.

    #endregion
}
