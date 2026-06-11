namespace GZCTF.Models.Transfer;

/// <summary>
/// Extension methods for converting between domain models and transfer models
/// </summary>
public static class TransferExtensions
{
    extension(Game game)
    {
        /// <summary>
        /// Convert Game to TransferGame
        /// </summary>
        public TransferGame ToTransfer()
        {
            var transfer = new TransferGame
            {
                Title = game.Title,
                Summary = game.Summary,
                Content = game.Content,
                Hidden = game.Hidden,
                PracticeMode = game.PracticeMode,
                AcceptWithoutReview = game.AcceptWithoutReview,
                InviteCode = game.InviteCode,
                TeamMemberCountLimit = game.TeamMemberCountLimit,
                ContainerCountLimit = game.ContainerCountLimit,
                StartTime = game.StartTimeUtc,
                EndTime = game.EndTimeUtc,
                Divisions = game.Divisions?.Select(d => d.ToTransfer()).ToList() ?? []
            };

            // Writeup configuration
            if (game.WriteupRequired || game.WriteupDeadline != DateTimeOffset.FromUnixTimeSeconds(0))
            {
                transfer.Writeup = new WriteupSection
                {
                    Required = game.WriteupRequired,
                    Deadline = game.WriteupDeadline,
                    Note = string.IsNullOrWhiteSpace(game.WriteupNote) ? null : game.WriteupNote
                };
            }

            // Blood bonus configuration
            var bloodBonus = game.BloodBonus;
            if (!bloodBonus.NoBonus)
            {
                transfer.BloodBonus = new BloodBonusSection
                {
                    First = (int)bloodBonus.FirstBlood,
                    Second = (int)bloodBonus.SecondBlood,
                    Third = (int)bloodBonus.ThirdBlood
                };
            }

            // Poster hash
            if (!string.IsNullOrWhiteSpace(game.PosterHash))
                transfer.PosterHash = game.PosterHash;

            // Event-wide A&D config — emit only when it deviates from the platform
            // defaults, so non-A&D game exports stay clean while configured A&D
            // games round-trip. (Runtime pause state is intentionally excluded.)
            if ((game.AdWarmupSeconds ?? 1800) != 1800
                || (game.AdTickSeconds ?? 60) != 60
                || (game.AdFlagLifetimeTicks ?? 5) != 5
                || (game.AdResetCooldownMinutes ?? 5) != 5
                || Math.Abs((game.AdGetflagWindowFraction ?? 0.5) - 0.5) > 1e-9
                || (game.AdMinGracePeriodSeconds ?? 3) != 3
                || !game.AdAllowSnapshotDownload
                || game.AdSnapshotRetentionDays is not null)
            {
                transfer.Ad = new TransferGame.AdGameSection
                {
                    WarmupSeconds = game.AdWarmupSeconds,
                    TickSeconds = game.AdTickSeconds,
                    FlagLifetimeTicks = game.AdFlagLifetimeTicks,
                    ResetCooldownMinutes = game.AdResetCooldownMinutes,
                    GetflagWindowFraction = game.AdGetflagWindowFraction,
                    MinGracePeriodSeconds = game.AdMinGracePeriodSeconds,
                    AllowSnapshotDownload = game.AdAllowSnapshotDownload,
                    SnapshotRetentionDays = game.AdSnapshotRetentionDays
                };
            }

            return transfer;
        }
    }

    extension(GameChallenge challenge)
    {
        /// <summary>
        /// Convert GameChallenge to TransferChallenge
        /// </summary>
        public TransferChallenge ToTransfer()
        {
            var transfer = new TransferChallenge
            {
                Id = challenge.Id,
                Title = challenge.Title,
                Content = challenge.Content,
                Category = challenge.Category,
                Type = challenge.Type,
                Enabled = challenge.IsEnabled,
                Scoring = new ScoringSection
                {
                    Original = challenge.OriginalScore,
                    MinRate = challenge.MinScoreRate,
                    Difficulty = challenge.Difficulty
                },
                Limits = new LimitsSection { Submission = challenge.SubmissionLimit, Deadline = challenge.DeadlineUtc },
                Flags = new FlagsSection
                {
                    Template = challenge.FlagTemplate,
                    DisableBloodBonus = challenge.DisableBloodBonus,
                    EnableTrafficCapture = challenge.EnableTrafficCapture,
                    EnableSharedContainer = challenge.EnableSharedContainer,
                    // For dynamic challenges with FlagTemplate, don't export the template flag to Static list
                    // to avoid duplication during import
                    Static = challenge.Flags
                        .Where(f => string.IsNullOrWhiteSpace(challenge.FlagTemplate) ||
                                    f.Flag != challenge.FlagTemplate)
                        .Select(f => new StaticFlagSection { Value = f.Flag, Attachment = f.Attachment?.ToTransfer() })
                        .ToList()
                }
            };

            // Hints
            if (challenge.Hints?.Count > 0)
            {
                transfer.Hints = challenge.Hints;
            }

            // Attachment
            if (challenge.Attachment != null)
            {
                transfer.Attachment = challenge.Attachment.ToTransfer();
            }

            // Container configuration (only for container challenges)
            if (challenge.Type.IsContainer())
            {
                transfer.Container = new ContainerSection
                {
                    Image = challenge.ContainerImage ?? string.Empty,
                    MemoryLimit = challenge.MemoryLimit ?? 64,
                    CpuCount = challenge.CPUCount ?? 1,
                    StorageLimit = challenge.StorageLimit ?? 256,
                    ExposePort = challenge.ExposePort ?? 80,
                    FileName = challenge.FileName,
                    NetworkMode = challenge.NetworkMode ?? NetworkMode.Open
                };
            }

            // Attack & Defense per-challenge config. NOTE: scoped to AttackDefense
            // (not UsesAdEngine) on purpose — the whole-game JSON transfer's
            // AdSection.CheckerImage is [Required], but KotH's checker is optional
            // (TCP-probe fallback), so emitting an empty-checker ad block for KotH
            // would fail re-import validation. KotH ad: knobs round-trip through
            // the challenge.yml path (ChallengeYamlModel) instead.
            if (challenge.Type.IsAttackDefense())
            {
                transfer.Ad = new AdSection
                {
                    CheckerImage = challenge.AdCheckerImage ?? string.Empty,
                    AllowEgress = challenge.AdAllowEgress,
                    AllowSelfReset = challenge.AdAllowSelfReset
                };
            }

            return transfer;
        }
    }

    extension(Attachment attachment)
    {
        /// <summary>
        /// Convert Attachment to TransferChallenge.AttachmentSection
        /// </summary>
        public AttachmentSection? ToTransfer()
        {
            if (attachment.Type == FileType.None)
                return null;

            var transfer = new AttachmentSection { Type = attachment.Type };

            switch (attachment.Type)
            {
                case FileType.Local when attachment.LocalFile != null:
                    transfer.Hash = attachment.LocalFile.Hash;
                    transfer.FileName = attachment.LocalFile.Name;
                    transfer.FileSize = attachment.LocalFile.FileSize;
                    break;
                case FileType.Remote:
                    transfer.RemoteUrl = attachment.RemoteUrl;
                    break;
                case FileType.None:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(attachment.Type));
            }

            return transfer;
        }
    }

    extension(Division division)
    {
        /// <summary>
        /// Convert Division to TransferDivision
        /// </summary>
        public TransferDivision ToTransfer() =>
            new()
            {
                Name = division.Name,
                InviteCode = division.InviteCode,
                DefaultPermissions = PermissionsToStrings(division.DefaultPermissions),
                ChallengeConfig = division.ChallengeConfigs
                    .Select(cc => new ChallengeConfigSection
                    {
                        ChallengeId = cc.ChallengeId,
                        Permissions = PermissionsToStrings(cc.Permissions)
                    })
                    .ToList()
            };
    }

    extension(TransferGame transfer)
    {
        /// <summary>
        /// Convert TransferGame to Game
        /// </summary>
        public Game ToGame(byte[]? xorKey = null)
        {
            var game = new Game
            {
                Title = transfer.Title,
                Summary = transfer.Summary,
                Content = transfer.Content,
                Hidden = transfer.Hidden,
                PracticeMode = transfer.PracticeMode,
                AcceptWithoutReview = transfer.AcceptWithoutReview,
                InviteCode = transfer.InviteCode,
                TeamMemberCountLimit = transfer.TeamMemberCountLimit,
                ContainerCountLimit = transfer.ContainerCountLimit,
                StartTimeUtc = transfer.StartTime,
                EndTimeUtc = transfer.EndTime,
                PosterHash = transfer.PosterHash
            };

            // Generate new key pair (always generate new keys on import)
            game.GenerateKeyPair(xorKey);

            // Writeup configuration
            if (transfer.Writeup != null)
            {
                game.WriteupRequired = transfer.Writeup.Required;
                game.WriteupDeadline = transfer.Writeup.Deadline;
                game.WriteupNote = transfer.Writeup.Note ?? string.Empty;
            }
            else
            {
                game.WriteupRequired = false;
                game.WriteupDeadline = DateTimeOffset.FromUnixTimeSeconds(0);
                game.WriteupNote = string.Empty;
            }

            // Blood bonus configuration
            if (transfer.BloodBonus != null)
            {
                var bloodBonusValue = ((long)transfer.BloodBonus.First << 20)
                                      + ((long)transfer.BloodBonus.Second << 10)
                                      + transfer.BloodBonus.Third;
                game.BloodBonusValue = bloodBonusValue;
            }
            else
            {
                game.BloodBonusValue = 0; // No bonus
            }

            // Event-wide A&D config — apply each provided field; omitted ones keep
            // the Game's platform defaults.
            if (transfer.Ad is { } ad)
            {
                if (ad.WarmupSeconds is { } ws) game.AdWarmupSeconds = ws;
                if (ad.TickSeconds is { } ts) game.AdTickSeconds = ts;
                if (ad.FlagLifetimeTicks is { } fl) game.AdFlagLifetimeTicks = fl;
                if (ad.ResetCooldownMinutes is { } rc) game.AdResetCooldownMinutes = rc;
                if (ad.GetflagWindowFraction is { } gw) game.AdGetflagWindowFraction = gw;
                if (ad.MinGracePeriodSeconds is { } mg) game.AdMinGracePeriodSeconds = mg;
                if (ad.AllowSnapshotDownload is { } asd) game.AdAllowSnapshotDownload = asd;
                if (ad.SnapshotRetentionDays is { } srd) game.AdSnapshotRetentionDays = srd;
                // Clamp to GameInfoModel.Validate's ranges — this path bypasses that validator.
                GZCTF.Utils.AdConfigBounds.Clamp(game);
            }

            return game;
        }
    }

    extension(TransferChallenge transfer)
    {
        /// <summary>
        /// Convert TransferChallenge to GameChallenge
        /// </summary>
        /// <remarks>
        /// Note: This does NOT set the GameId or create the attachment/flag entities.
        /// Those should be handled separately during the import process.
        /// </remarks>
        public GameChallenge ToChallenge()
        {
            var challenge = new GameChallenge
            {
                Title = transfer.Title,
                Content = transfer.Content,
                Category = transfer.Category,
                Type = transfer.Type,
                IsEnabled = transfer.Enabled,
                OriginalScore = transfer.Scoring.Original,
                MinScoreRate = transfer.Scoring.MinRate,
                Difficulty = transfer.Scoring.Difficulty,
                SubmissionLimit = transfer.Limits.Submission,
                DeadlineUtc = transfer.Limits.Deadline,
                FlagTemplate = transfer.Flags.Template,
                DisableBloodBonus = transfer.Flags.DisableBloodBonus,
                EnableTrafficCapture = transfer.Flags.EnableTrafficCapture,
                // Only StaticContainer honors a shared container (guarded again at create time).
                EnableSharedContainer =
                    transfer.Type == ChallengeType.StaticContainer && transfer.Flags.EnableSharedContainer,
                Hints = transfer.Hints
            };

            // Container configuration
            if (transfer.Container != null)
            {
                challenge.ContainerImage = transfer.Container.Image;
                challenge.MemoryLimit = transfer.Container.MemoryLimit;
                challenge.CPUCount = transfer.Container.CpuCount;
                challenge.StorageLimit = transfer.Container.StorageLimit;
                challenge.ExposePort = transfer.Container.ExposePort;
                challenge.FileName = transfer.Container.FileName;
                challenge.NetworkMode = transfer.Container.NetworkMode;
            }

            // Attack & Defense config — only when type is AttackDefense AND ad
            // section was provided. Validation in TransferChallenge.Validate()
            // catches the AttackDefense-without-ad case before we get here.
            // (KotH ad: knobs travel via the challenge.yml path, not this
            // whole-game JSON transfer — see ToTransfer above.)
            if (transfer.Type.IsAttackDefense() && transfer.Ad is { } ad)
            {
                challenge.AdCheckerImage = ad.CheckerImage;
                if (ad.AllowEgress is { } ae) challenge.AdAllowEgress = ae;
                if (ad.AllowSelfReset is { } asr) challenge.AdAllowSelfReset = asr;
            }

            return challenge;
        }
    }

    extension(StaticFlagSection transfer)
    {
        /// <summary>
        /// Create FlagContext from static flag transfer
        /// </summary>
        public FlagContext ToFlagContext() =>
            new() { Flag = transfer.Value, IsOccupied = false };
    }

    /// <summary>
    /// Convert GamePermission enum to string array
    /// </summary>
    public static List<string> PermissionsToStrings(GamePermission permissions)
    {
        switch (permissions)
        {
            // Special cases
            case GamePermission.All:
                return ["All"];
            case 0:
                return ["None"];
        }

        // Extract individual flags
        var result = from value in Enum.GetValues<GamePermission>()
                     where value is not (0 or GamePermission.All)
                     where permissions.HasFlag(value)
                     select value.ToString();

        return result.ToList();
    }

    /// <summary>
    /// Convert string array to GamePermission enum
    /// </summary>
    public static GamePermission StringsToPermissions(List<string>? permissionStrings)
    {
        if (permissionStrings == null || permissionStrings.Count == 0)
            return 0;

        // Special cases
        if (permissionStrings.Contains("All"))
            return GamePermission.All;

        if (permissionStrings.Contains("None"))
            return 0;

        // Combine individual flags
        var validPermissions = permissionStrings
            .Select(str => Enum.TryParse<GamePermission>(str, out var value) ? value : (GamePermission?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value);

        return validPermissions.Aggregate<GamePermission, GamePermission>(0,
            (current, value) => current | value);
    }
}
