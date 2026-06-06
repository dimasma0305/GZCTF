using System.Text.Json.Serialization;
using GZCTF.Utils;

namespace GZCTF.Models.Internal;

public class CheatReport
{
    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("ipAnalysis")]
    public List<IpAnalysisResult> IpAnalysis { get; set; } = new();

    [JsonPropertyName("abnormalSolves")]
    public List<AbnormalSolveResult> AbnormalSolves { get; set; } = new();

    [JsonPropertyName("suspicionList")]
    public List<SuspicionRecordResult> SuspicionList { get; set; } = new();

    [JsonPropertyName("collusionGroups")]
    public List<CollusionGroupResult> CollusionGroups { get; set; } = new();

    /// Cross-team identity/network overlap (same fingerprint or IP used by
    /// multiple teams). Non-scoring — surfaced for human review only, so the
    /// account-sharing signal isn't lost now that identity signals score 0.
    [JsonPropertyName("identityOverlaps")]
    public List<IdentityOverlapResult> IdentityOverlaps { get; set; } = new();
}

public class IpAnalysisResult
{
    [JsonPropertyName("teamId")]
    public int TeamId { get; set; }

    [JsonPropertyName("teamName")]
    public string TeamName { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("ip")]
    public string Ip { get; set; } = string.Empty;

    [JsonPropertyName("time")]
    public DateTimeOffset Time { get; set; }

    [JsonPropertyName("details")]
    public string Details { get; set; } = string.Empty;

    [JsonPropertyName("relatedTeams")]
    public List<string> RelatedTeams { get; set; } = new();

    [JsonPropertyName("userNames")]
    public List<string> UserNames { get; set; } = new();

    [JsonPropertyName("relatedUsers")]
    public List<string> RelatedUsers { get; set; } = new();
}

public class AbnormalSolveResult
{
    [JsonPropertyName("teamId")]
    public int TeamId { get; set; }

    [JsonPropertyName("teamName")]
    public string TeamName { get; set; } = string.Empty;

    [JsonPropertyName("challengeId")]
    public int ChallengeId { get; set; }

    [JsonPropertyName("challengeName")]
    public string ChallengeName { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("details")]
    public string Details { get; set; } = string.Empty;

    [JsonPropertyName("solveTime")]
    public DateTimeOffset SolveTime { get; set; }
}

public class SuspicionRecordResult
{
    [JsonPropertyName("teamId")]
    public int TeamId { get; set; }

    [JsonPropertyName("participationId")]
    public int ParticipationId { get; set; }

    [JsonPropertyName("teamName")]
    public string TeamName { get; set; } = string.Empty;

    /// Total tiered risk score (Hard + Corroboration + Strong + Behavioral).
    [JsonPropertyName("score")]
    public int Score { get; set; }

    /// Risk band: evidenced | investigate | watch | context | clean.
    /// This — not the raw number — is the headline classification.
    [JsonPropertyName("band")]
    public string Band { get; set; } = "clean";

    [JsonPropertyName("hard")]
    public int Hard { get; set; }

    [JsonPropertyName("strong")]
    public int Strong { get; set; }

    [JsonPropertyName("behavioral")]
    public int Behavioral { get; set; }

    [JsonPropertyName("corroboration")]
    public int Corroboration { get; set; }

    [JsonPropertyName("status")]
    public ParticipationStatus Status { get; set; }

    [JsonPropertyName("events")]
    public List<SuspicionEventResult> Events { get; set; } = new();
}

public class SuspicionEventResult
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("scoreDelta")]
    public int ScoreDelta { get; set; }

    [JsonPropertyName("details")]
    public string Details { get; set; } = string.Empty;

    [JsonPropertyName("time")]
    public DateTimeOffset Time { get; set; }

    /// Evidence tier: context | behavioral | strong | hard.
    [JsonPropertyName("tier")]
    public string Tier { get; set; } = "behavioral";

    /// True if this event actually contributed to the score (false for context
    /// signals and for repeat incidents beyond the per-rule cap).
    [JsonPropertyName("counted")]
    public bool Counted { get; set; }
}

public class IdentityOverlapResult
{
    /// "fingerprint" or "ip".
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// The shared value, masked for display (e.g. fingerprint prefix or IP).
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;

    [JsonPropertyName("teamCount")]
    public int TeamCount { get; set; }

    [JsonPropertyName("teamNames")]
    public List<string> TeamNames { get; set; } = new();

    [JsonPropertyName("userNames")]
    public List<string> UserNames { get; set; } = new();
}


public class CollusionGroupResult
{
    [JsonPropertyName("teams")]
    public List<CollusionTeamInfo> Teams { get; set; } = new();

    [JsonPropertyName("averageRsi")]
    public double AverageRSI { get; set; }

    [JsonPropertyName("commonSolves")]
    public List<string> CommonSolves { get; set; } = new();

    [JsonPropertyName("details")]
    public string Details { get; set; } = string.Empty;

    [JsonPropertyName("detailedSolves")]
    public List<SequenceSuspectDetail> DetailedSolves { get; set; } = new();
}

public class SequenceSuspectDetail
{
    [JsonPropertyName("challengeName")]
    public string ChallengeName { get; set; } = string.Empty;

    [JsonPropertyName("timeA")]
    public DateTimeOffset TimeA { get; set; }

    [JsonPropertyName("timeB")]
    public DateTimeOffset TimeB { get; set; }

    [JsonPropertyName("timeDiff")]
    public double TimeDiff { get; set; }
}

public class CollusionCompareResult
{
    [JsonPropertyName("rsi")]
    public double RSI { get; set; }

    [JsonPropertyName("details")]
    public List<SequenceSuspectDetail> Details { get; set; } = new();
}

public class CollusionTeamInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("participationId")]
    public int ParticipationId { get; set; }
}
