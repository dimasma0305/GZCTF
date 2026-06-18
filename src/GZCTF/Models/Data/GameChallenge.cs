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
    /// Whether all teams share a single container instance instead of one per team.
    /// Only valid for <see cref="ChallengeType.StaticContainer"/> (one shared static flag).
    /// Saves resources when per-team isolation isn't needed. The shared container is created
    /// lazily on first start, reclaimed by the idle-timeout cron, and re-created on demand.
    /// </summary>
    public bool EnableSharedContainer { get; set; }

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
        AdSelfHosted = model.AdSelfHosted ?? AdSelfHosted;

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

        // Shared instance is only meaningful for StaticContainer (single shared static flag);
        // force it off for every other type so a stale toggle can't take effect after a retype.
        EnableSharedContainer = Type == ChallengeType.StaticContainer
                                && (model.EnableSharedContainer ?? EnableSharedContainer);
    }

    /// <summary>
    /// True when this challenge serves one shared container to all teams (StaticContainer +
    /// <see cref="EnableSharedContainer"/> + a valid image/port).
    /// </summary>
    [NotMapped]
    public bool UsesSharedContainer =>
        Type == ChallengeType.StaticContainer
        && EnableSharedContainer
        && !string.IsNullOrEmpty(ContainerImage)
        && ExposePort is not null;

    #region Db Relationship

    /// <summary>
    /// The single shared container's id, when <see cref="UsesSharedContainer"/>. Points at a
    /// <see cref="Container"/> row that has no GameInstance (challenge-owned, not team-owned).
    /// A plain pointer (no FK): when the idle cron reaps the container the pointer is left
    /// dangling and the get-or-create path treats a missing row as "none" and re-creates.
    /// </summary>
    public Guid? SharedContainerId { get; set; }

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

    /// <summary>
    /// A&D / KotH only: when true, GZCTF does NOT host the team's service container.
    /// Instead the team runs the service on their own machine ("bring your own
    /// container") and connects it into the game network through a GZCTF-managed relay
    /// endpoint on the normal challenge bridge, so the checker, attack proxy, egress
    /// isolation, and flag rotation all continue to work unchanged against that relay.
    /// Default false — GZCTF hosts the container as usual.
    /// </summary>
    public bool AdSelfHosted { get; set; }

    // Tick length, flag lifetime, reset cooldown, snapshot-download, and the
    // checker timing knobs (getflag jitter window + min grace period) are all
    // EVENT-WIDE policy and live on Game (AdTickSeconds, AdFlagLifetimeTicks,
    // AdResetCooldownMinutes, AdAllowSnapshotDownload, AdGetflagWindowFraction,
    // AdMinGracePeriodSeconds). Rounds span the whole game, so a per-challenge
    // tick was never actually honored, and the jitter/grace are fractions of
    // that shared tick — the random offset is still rolled per (team, service,
    // round), so anti-fingerprinting is unaffected by sharing one window size.

    #endregion

    /// <summary>
    /// True when this A&amp;D / KotH challenge may be launched as a standalone
    /// <em>practice</em> container — i.e. a normal per-team instance with its own
    /// connection address, like a DynamicContainer — rather than the live
    /// defending-service / hill model. Only after the game has ENDED and only in
    /// practice mode, and only if it actually ships a container image+port. The
    /// live A&amp;D engine owns these challenges during the game (rounds, checker,
    /// per-team service); once it's over they fall back to the standard container
    /// flow so players can keep practising. Gating every call site on this keeps
    /// the feature completely inert while a game is running.
    /// </summary>
    public bool AllowsPracticeContainer(Game game) =>
        Type.UsesAdEngine()
        && game.PracticeMode
        && DateTimeOffset.UtcNow > game.EndTimeUtc
        && !string.IsNullOrEmpty(ContainerImage)
        && ExposePort is not null;
}
