using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using GZCTF.Models.Request.Edit;
using MemoryPack;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities.Encoders;

namespace GZCTF.Models.Data;

[MemoryPackable]
public partial class Game
{
    [Key]
    [Required]
    public int Id { get; set; }

    /// <summary>
    /// Game title
    /// </summary>
    [Required]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Token signature public key
    /// </summary>
    [Required]
    [MaxLength(Limits.GameKeyLength)]
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Token signature private key
    /// </summary>
    [Required]
    [MaxLength(Limits.GameKeyLength)]
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// Whether to hide
    /// </summary>
    [Required]
    public bool Hidden { get; set; }

    /// <summary>
    /// Whether the game is in practice mode (most operations can still be performed after the game ends)
    /// </summary>
    public bool PracticeMode { get; set; } = true;

    /// <summary>
    /// Poster hash
    /// </summary>
    [MaxLength(Limits.FileHashLength)]
    public string? PosterHash { get; set; }

    /// <summary>
    /// Game description
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Detailed introduction of the game
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Teams can join without review
    /// </summary>
    public bool AcceptWithoutReview { get; set; }

    /// <summary>
    /// Whether logged-in users may submit challenges (which then sit in the
    /// admin review queue). Default false — community submissions are
    /// opt-in per game so a fresh event doesn't accept arbitrary uploads
    /// before the admin has decided to enable the queue.
    /// </summary>
    [Required]
    public bool AllowUserSubmissions { get; set; } = false;

    /// <summary>
    /// Whether writeup is required
    /// </summary>
    public bool WriteupRequired { get; set; }

    /// <summary>
    /// Game invitation code
    /// </summary>
    [MaxLength(Limits.InviteTokenLength)]
    public string? InviteCode { get; set; }

    /// <summary>
    /// Limit on the number of team members, 0 means no limit
    /// </summary>
    public int TeamMemberCountLimit { get; set; }

    /// <summary>
    /// Discord webhook URL
    /// </summary>
    [MaxLength(Limits.UrlLength)]
    public string? DiscordWebhook { get; set; }

    /// <summary>
    /// Limit on the number of containers a team can have simultaneously
    /// </summary>
    public int ContainerCountLimit { get; set; } = 3;

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
    /// Writeup submission deadline
    /// </summary>
    [Required]
    public DateTimeOffset WriteupDeadline { get; set; } = DateTimeOffset.FromUnixTimeSeconds(0);

    /// <summary>
    /// Optional scoreboard freeze time (ICPC-style). When set and the current time is in
    /// [FreezeTimeUtc, EndTimeUtc), non-monitor viewers see a frozen snapshot of the scoreboard.
    /// Submissions made during the freeze still persist and are scored; only the public view is frozen.
    /// </summary>
    public DateTimeOffset? FreezeTimeUtc { get; set; }

    /// <summary>
    /// Additional notes for writeup
    /// </summary>
    [Required]
    public string WriteupNote { get; set; } = string.Empty;

    [JsonIgnore]
    [Column(nameof(BloodBonus))]
    public long BloodBonusValue { get; set; } = BloodBonus.DefaultValue;

    /// <summary>
    /// Blood bonus
    /// </summary>
    [NotMapped]
    [Required]
    [MemoryPackIgnore]
    public BloodBonus BloodBonus
    {
        get => BloodBonus.FromValue(BloodBonusValue);
        set => BloodBonusValue = value.Val;
    }

    /// <summary>
    /// Whether the game is active
    /// </summary>
    [NotMapped]
    [JsonIgnore]
    [MemoryPackIgnore]
    public bool IsActive => StartTimeUtc <= DateTimeOffset.Now && DateTimeOffset.Now <= EndTimeUtc;

    /// <summary>
    /// Poster URL
    /// </summary>
    [NotMapped]
    [MemoryPackIgnore]
    public string? PosterUrl => GetPosterUrl(PosterHash);

    /// <summary>
    /// Team hash salt
    /// </summary>
    [NotMapped]
    [MemoryPackIgnore]
    public string TeamHashSalt => $"GZCTF@{PrivateKey}@PK".ToSHA256String();

    internal static string? GetPosterUrl(string? hash) => hash is null ? null : $"/assets/{hash}/poster";

    internal void GenerateKeyPair(byte[]? xorKey)
    {
        SecureRandom sr = new();
        Ed25519KeyPairGenerator kpg = new();
        kpg.Init(new Ed25519KeyGenerationParameters(sr));
        var kp = kpg.GenerateKeyPair();
        var privateKey = (Ed25519PrivateKeyParameters)kp.Private;
        var publicKey = (Ed25519PublicKeyParameters)kp.Public;

        PrivateKey =
            Base64.ToBase64String(xorKey is null
                ? privateKey.GetEncoded()
                : Codec.Xor(privateKey.GetEncoded(), xorKey));

        PublicKey = Base64.ToBase64String(publicKey.GetEncoded());
    }

    internal string Sign(string str, byte[]? xorKey)
    {
        Ed25519PrivateKeyParameters privateKey;
        if (xorKey is null)
            privateKey = new(Codec.Base64.DecodeToBytes(PrivateKey), 0);
        else
            privateKey = new(Codec.Xor(Codec.Base64.DecodeToBytes(PrivateKey), xorKey), 0);

        return CryptoUtils.GenerateSignature(str, privateKey, SignAlgorithm.Ed25519);
    }

    internal Game Update(GameInfoModel model)
    {
        Title = model.Title;
        Content = model.Content;
        Summary = model.Summary;
        Hidden = model.Hidden;
        PracticeMode = model.PracticeMode;
        AcceptWithoutReview = model.AcceptWithoutReview;
        AllowUserSubmissions = model.AllowUserSubmissions;
        InviteCode = model.InviteCode;
        EndTimeUtc = model.EndTimeUtc;
        StartTimeUtc = model.StartTimeUtc;
        TeamMemberCountLimit = model.TeamMemberCountLimit;
        ContainerCountLimit = model.ContainerCountLimit;
        WriteupNote = model.WriteupNote;
        WriteupRequired = model.WriteupRequired;
        WriteupDeadline = model.WriteupDeadline;
        FreezeTimeUtc = model.FreezeTimeUtc;
        BloodBonus = BloodBonus.FromValue(model.BloodBonusValue);
        DiscordWebhook = model.DiscordWebhook;
        // A&D — only overwrite when caller provides; null leaves existing default.
        if (model.AdWarmupSeconds is { } warmup) AdWarmupSeconds = warmup;
        if (model.AdSnapshotRetentionDays is { } retention) AdSnapshotRetentionDays = retention;
        if (model.AdTickSeconds is { } tick) AdTickSeconds = tick;
        if (model.AdFlagLifetimeTicks is { } lifetime) AdFlagLifetimeTicks = lifetime;
        if (model.AdResetCooldownMinutes is { } cooldown) AdResetCooldownMinutes = cooldown;
        if (model.AdAllowSnapshotDownload is { } snap) AdAllowSnapshotDownload = snap;

        return this;
    }

    #region Db Relationship

    /// <summary>
    /// Game events
    /// </summary>
    [JsonIgnore]
    public List<GameEvent> GameEvents { get; set; } = [];

    /// <summary>
    /// Game notices
    /// </summary>
    [JsonIgnore]
    public List<GameNotice> GameNotices { get; set; } = [];

    /// <summary>
    /// Game submissions
    /// </summary>
    [JsonIgnore]
    public List<Submission> Submissions { get; set; } = [];

    /// <summary>
    /// Game challenges
    /// </summary>
    [JsonIgnore]
    public HashSet<GameChallenge> Challenges { get; set; } = [];

    /// <summary>
    /// Game participations
    /// </summary>
    [JsonIgnore]
    public HashSet<Participation> Participations { get; set; } = [];

    /// <summary>
    /// Game teams
    /// </summary>
    [JsonIgnore]
    public HashSet<Team>? Teams { get; set; }

    /// <summary>
    /// List of divisions for the game
    /// </summary>
    public HashSet<Division>? Divisions { get; set; }

    /// <summary>
    /// Set when this game was auto-created by a <see cref="GameRepoBinding"/>
    /// scan; null for hand-created games. Lets the discovery service find
    /// and update its own children on re-scan.
    /// </summary>
    public int? RepoBindingId { get; set; }

    [JsonIgnore]
    [MemoryPackIgnore]
    public GameRepoBinding? RepoBinding { get; set; }

    /// <summary>
    /// Repo-relative path of the <c>.gzevent</c> file that defined this
    /// game (e.g. <c>quals/.gzevent</c>). Unique within a binding.
    /// </summary>
    [MaxLength(512)]
    public string? EventManifestPath { get; set; }

    #endregion Db Relationship

    #region Attack & Defense fields
    // Nullable — only consulted when the game has any AttackDefense challenge.

    /// <summary>
    /// Warm-up window in seconds before the first A&amp;D round starts. Default
    /// 1800 (30 min). Teams get the gap between StartTimeUtc and StartTimeUtc +
    /// AdWarmupSeconds to SSH in, read code, write initial patches without
    /// scoring or attacks counting. Industry norm.
    /// </summary>
    public int? AdWarmupSeconds { get; set; } = 1800;

    /// <summary>
    /// Seconds per tick — the global scoring unit. The checker runs once per
    /// (team, service) per tick and flags rotate at tick boundaries.
    /// Event-wide: every A&amp;D service in the game shares one tick (rounds
    /// span the whole game), so this is a game knob, not a per-challenge one.
    /// Default 120. Industry norm is 60–180s.
    /// </summary>
    public int? AdTickSeconds { get; set; } = 120;

    /// <summary>
    /// Number of ticks a planted flag remains valid for attack submission —
    /// the uniform attack window across the event. Default 5.
    /// </summary>
    public int? AdFlagLifetimeTicks { get; set; } = 5;

    /// <summary>
    /// Minimum minutes between consecutive self-resets of a team's container.
    /// Event-wide anti-spam fairness policy. Default 5. (Whether a given
    /// service can be reset at all is the per-challenge
    /// <c>GameChallenge.AdAllowSelfReset</c> flag.)
    /// </summary>
    public int? AdResetCooldownMinutes { get; set; } = 5;

    /// <summary>
    /// If true, each team's final container state is committed + saved as a
    /// gzipped tarball at game end and made available for download. Event-wide
    /// policy; pairs with <see cref="AdSnapshotRetentionDays"/>. Default true.
    /// </summary>
    public bool AdAllowSnapshotDownload { get; set; } = true;

    /// <summary>
    /// How long to retain per-team container snapshots (the tarballs produced
    /// at game end when <see cref="AdAllowSnapshotDownload"/>) before
    /// the cleanup job expires them. Null (default) = keep forever — operators
    /// opt-in to expiration explicitly. Any positive integer = retain N days
    /// after game end.
    /// </summary>
    public int? AdSnapshotRetentionDays { get; set; }
    #endregion
}
