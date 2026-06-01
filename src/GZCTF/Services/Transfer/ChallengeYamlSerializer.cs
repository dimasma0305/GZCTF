using GZCTF.Models.Data;
using GZCTF.Models.Request.Edit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GZCTF.Services.Transfer;

/// <summary>
/// Round-trip the in-memory <see cref="GameChallenge"/> row back to a
/// <c>challenge.yml</c> file. Output uses the camelCase aliases the
/// parser already expects (see
/// <see cref="ChallengeYamlModel"/> — same property metadata), so a
/// re-import of a serialized yaml yields the same DB state.
///
/// <para><b>Lossy:</b> comments and unrecognized keys in the original
/// file are NOT preserved — only the fields we model survive. Operators
/// that opt into <c>PushOnEdit</c> accept this trade-off.</para>
///
/// <para><b>Excluded:</b> platform-managed state never lands in yaml —
/// <c>BuildStatus</c>, <c>BuildImageDigest</c>, <c>LastBuildLog</c>,
/// <c>OriginalScore</c>, <c>MinScore</c>, <c>OriginalArchiveBlobPath</c>.
/// Those drift from the repo as the platform manages them and would
/// cause noisy diff churn if we pushed them.</para>
/// </summary>
public static class ChallengeYamlSerializer
{
    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        // YamlDotNet writes nulls as "key:" lines, which both clutters
        // the file and re-parses to empty strings on the next read.
        // Skip them entirely.
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull
                                        | DefaultValuesHandling.OmitEmptyCollections)
        .Build();

    /// <summary>
    /// Serialize a challenge + its flags into a yaml string suitable
    /// for writing back to <see cref="GameChallenge.SourceYamlPath"/>.
    /// </summary>
    /// <param name="ch">The challenge entity with its
    /// <c>Flags</c> navigation loaded.</param>
    /// <param name="flagTexts">Flag literal strings (extracted by the
    /// caller from the FlagContext entities; we don't take the nav
    /// directly so the caller can decide how to render dynamic flag
    /// templates).</param>
    public static string Serialize(GameChallenge ch, IReadOnlyList<string> flagTexts)
    {
        var model = new ChallengeYamlModel
        {
            Name = ch.Title,
            // Author is split out of Content at import time by
            // ApplyYamlToChallenge ("Author: **X**\n\n..."), but we
            // don't reverse that here — round-tripping author back
            // through Content would require fragile string parsing.
            // Leave Author null and keep the existing Content;
            // operators editing the description still get a clean
            // round trip on everything else.
            Description = StripAuthorPrefix(ch.Content, out var extractedAuthor),
            Author = extractedAuthor,
            Type = ch.Type.ToString(),
            Category = ch.Category.ToString(),
            FlagTemplate = string.IsNullOrEmpty(ch.FlagTemplate) ? null : ch.FlagTemplate,
            Hints = ch.Hints is { Count: > 0 } ? new List<string>(ch.Hints) : null,
            Flags = flagTexts.Count > 0 ? new List<string>(flagTexts) : null,
            // Defaults match GameChallenge entity init (0.25 / 5) —
            // omit when the row still carries the default so we don't
            // emit noisy "field: 0.25" / "difficulty: 5" lines after
            // a fresh import.
            MinScoreRate = ch.MinScoreRate == 0.25 ? null : ch.MinScoreRate,
            Difficulty = ch.Difficulty == 5 ? null : ch.Difficulty,
            SubmissionLimit = ch.SubmissionLimit == 0 ? null : ch.SubmissionLimit,
            DisableBloodBonus = ch.DisableBloodBonus ? true : null,
            // FileName isn't in ChallengeYamlModel — the `provide:` field
            // points at the attachment file path, and we don't track the
            // attachment's relative path round-trip, so leave it as the
            // existing yaml's value (gets stripped by re-serialization).
            // Operators wanting attachment changes via push-back is a
            // separate feature.
        };

        if (ch.Type.IsContainer())
        {
            model.Container = new ChallengeYamlModel.ContainerSection
            {
                // Never write back a platform-AUTO-BUILT tag (gzctf-auto/{game}/{slug}:{sha}).
                // The repo omits containerImage on purpose so the importer auto-builds
                // ./src/Dockerfile; baking the built tag into the pushed yaml would make the
                // next sync see a "registry image", flip BuildStatus to NotApplicable, and
                // stop the challenge from ever rebuilding. Omit it so the build intent
                // round-trips. A genuine operator-pinned registry ref (nginx:alpine,
                // ghcr.io/...) is preserved.
                ContainerImage = IsAutoBuiltTag(ch.ContainerImage) ? null : ch.ContainerImage,
                MemoryLimit = ch.MemoryLimit,
                CpuCount = ch.CPUCount,
                StorageLimit = ch.StorageLimit,
                ExposePort = ch.ExposePort,
                NetworkMode = ch.NetworkMode == GZCTF.Utils.NetworkMode.Open
                    ? null
                    : ch.NetworkMode.ToString(),
                EnableTrafficCapture = ch.EnableTrafficCapture ? true : null,
                FlagTemplate = string.IsNullOrEmpty(ch.FlagTemplate) ? null : ch.FlagTemplate,
            };
        }

        // A&D-engine block (AttackDefense + KingOfTheHill) — emit only NON-default
        // per-challenge knobs (defaults match the GameChallenge entity init:
        // egress/self-reset true, jitter 0.4/0.5, grace 3) so a fresh import
        // doesn't churn the file, and skip the block entirely when nothing
        // differs. Must cover KotH so a re-exported hill round-trips its
        // allowEgress:false / checkerImage. Event-wide settings live on the game
        // (.gzevent), not here.
        if (ch.Type.UsesAdEngine())
        {
            var ad = new ChallengeYamlModel.AdSection
            {
                // Same as the service image: an auto-built checker (gzctf-auto/.../-checker:sha,
                // built from ./checker on import) must NOT be pinned back into the yaml, or the
                // next sync stops auto-building it. A pinned registry checker is preserved.
                CheckerImage = (string.IsNullOrEmpty(ch.AdCheckerImage) || IsAutoBuiltTag(ch.AdCheckerImage))
                    ? null : ch.AdCheckerImage,
                AllowEgress = ch.AdAllowEgress ? null : false,
                AllowSelfReset = ch.AdAllowSelfReset ? null : false,
                SshRequiresFlag = ch.AdSshRequiresFlag ? true : null,
            };
            if (ad.CheckerImage is not null || ad.AllowEgress is not null
                || ad.AllowSelfReset is not null || ad.SshRequiresFlag is not null)
                model.Ad = ad;
        }

        return YamlSerializer.Serialize(model);
    }

    /// <summary>
    /// True for a platform auto-built image tag (<c>gzctf-auto/{game}/{slug}[-checker]:{sha}</c>).
    /// These are generated from a Dockerfile in the package on every import and are not part of
    /// the authored source, so they must never be serialized back into the pushed yaml — doing so
    /// turns a "build me" challenge into a "pull this registry image" one on the next sync.
    /// </summary>
    private static bool IsAutoBuiltTag(string? image) =>
        !string.IsNullOrEmpty(image) && image.StartsWith("gzctf-auto/", StringComparison.Ordinal);

    /// <summary>
    /// The importer prepends <c>"Author: **X**\n\n"</c> to the
    /// challenge description when an <c>author:</c> field is present in
    /// the yaml. Reverse that for the round trip so we don't double the
    /// prefix on every push.
    /// </summary>
    private static string StripAuthorPrefix(string content, out string? author)
    {
        author = null;
        if (string.IsNullOrEmpty(content)) return content;
        // Conservative: only match the exact shape the importer writes.
        const string prefix = "Author: **";
        if (!content.StartsWith(prefix, StringComparison.Ordinal)) return content;
        var endQuote = content.IndexOf("**\n\n", prefix.Length, StringComparison.Ordinal);
        if (endQuote < 0) return content;
        author = content[prefix.Length..endQuote];
        return content[(endQuote + 4)..];
    }
}
