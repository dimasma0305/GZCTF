namespace GZCTF.Models.Internal;

public static class SuspicionType
{
    public const string StolenFlag = "StolenFlag";
    public const string SharedIP = "SharedIP";
    public const string SharedFingerprint = "SharedFingerprint";
    public const string FingerprintChurn = "FingerprintChurn";
    public const string IpChurn = "IpChurn";
    public const string UnknownIP = "UnknownIP";
    public const string CrossTeamIP = "CrossTeamIP";
    public const string TokenAbuse = "TokenAbuse";
    public const string Hoarding = "Hoarding";
    public const string Burst = "Burst";
    public const string NoDownload = "NoDownload";
    public const string NoContainer = "NoContainer";
    public const string FastSolveOpen = "FastSolve-Open";
    public const string FastSolveDownload = "FastSolve-Download";
    public const string FastSolveContainer = "FastSolve-Container";
    public const string SequenceSimilarity = "SequenceSimilarity";
    public const string CollusionGroup = "CollusionGroup";

    // New signals
    public const string ZeroWrongAttempts = "ZeroWrongAttempts";
    public const string WrongFlagLeakage = "WrongFlagLeakage";
    public const string SolutionRelay = "SolutionRelay";
    public const string AdaptiveFastSolve = "AdaptiveFastSolve";
    public const string DirectedSolving = "DirectedSolving";
    public const string ClusteredRegistration = "ClusteredRegistration";
    public const string SubnetOverlap = "SubnetOverlap";
    public const string HighWrongRate = "HighWrongRate";
    public const string AutomatedPattern = "AutomatedPattern";
    public const string SessionConcurrency = "SessionConcurrency";
    public const string FirstBloodAnomaly = "FirstBloodAnomaly";

    // Inspector signals — automated-tool / scanner detection
    public const string HoneypotHit = "HoneypotHit";
    public const string HoneypotProtocolHit = "HoneypotProtocolHit";
    public const string HoneypotCanaryFlag = "HoneypotCanaryFlag";
    public const string HoneypotChain = "HoneypotChain";
    public const string FlagEgress = "FlagEgress";

    // Container-access signals — derived from ContainerAccessEvent rows
    public const string CrossTeamContainerAccess        = "CrossTeamContainerAccess";
    public const string DelayedSolveSubmission          = "DelayedSolveSubmission";
    public const string InstantSubmitAfterAccess        = "InstantSubmitAfterAccess";
    public const string SubmitterNeverAccessedContainer = "SubmitterNeverAccessedContainer";
    public const string AccessIpMismatchAtSubmission    = "AccessIpMismatchAtSubmission";

    public static readonly Dictionary<string, (int Weight, string Description)> Defaults = new()
    {
        { StolenFlag, (100, "Flag stolen from another team") },
        { SharedIP, (10, "Multiple team members using same IP") },
        { SharedFingerprint, (60, "Multiple users with same browser fingerprint") },
        { FingerprintChurn, (30, "Single user using many different browser fingerprints") },
        { IpChurn, (20, "Single user using many different IP addresses") },
        { UnknownIP, (10, "Using IP not seen in game before") },
        { CrossTeamIP, (20, "IP used by members from multiple teams") },
        { TokenAbuse, (80, "Multiple people using same submission token") },
        { Hoarding, (30, "Solved challenge long after container destroy") },
        { Burst, (30, "Multiple challenges solved in a very short time") },
        { NoDownload, (80, "Solved without downloading attachment") },
        { NoContainer, (80, "Solved without starting container") },
        { FastSolveOpen, (50, "Solved very quickly after opening challenge") },
        { FastSolveDownload, (50, "Solved very quickly after downloading attachment") },
        { FastSolveContainer, (50, "Solved very quickly after starting container") },
        { SequenceSimilarity, (40, "High similarity in solve order and timing") },
        { CollusionGroup, (10, "Member of a detected collusion group") },
        { ZeroWrongAttempts, (50, "Solved dynamic challenge on first attempt with no wrong submissions") },
        { WrongFlagLeakage, (80, "Submitted another team's valid dynamic flag as a wrong answer") },
        { SolutionRelay, (60, "Consistently solves challenges shortly after another team with constant lag") },
        { AdaptiveFastSolve, (60, "Solved far faster than the community median solve time") },
        { DirectedSolving, (30, "Only opened challenges they solved — no exploratory browsing") },
        { ClusteredRegistration, (40, "Multiple team accounts registered from the same IP within 48h") },
        { SubnetOverlap, (5, "Teams share the same /24 subnet") },
        { HighWrongRate, (40, "Burst of wrong flag submissions — possible brute force") },
        { AutomatedPattern, (50, "Machine-speed flag submission intervals — likely scripted") },
        { SessionConcurrency, (30, "Same user account active from two different IPs within 10 minutes") },
        { FirstBloodAnomaly, (20, "First blood on a hard challenge not solved by others for 2+ hours") },
        { HoneypotHit, (70, "Hit a platform honeypot HTTP route — automated reconnaissance") },
        { HoneypotProtocolHit, (90, "Connected to a platform honeypot protocol service (SSH, Redis, etc.) — broad infra scan") },
        { HoneypotCanaryFlag, (100, "Submitted a canary flag exposed only via honeypot — automated scrape pipeline") },
        { HoneypotChain, (150, "Followed multiple cross-referenced honeypot baits — automated link-following scanner or agent") },
        { FlagEgress, (80, "Team flag observed in proxied container traffic — exfil pipeline or automated solver") },
        { CrossTeamContainerAccess,        (120, "A non-admin user from a different team opened the proxy WebSocket on this team's container") },
        { DelayedSolveSubmission,          ( 40, "Submitter personally opened the container long before they submitted the flag") },
        { InstantSubmitAfterAccess,        ( 50, "Submission within seconds of the submitter's first proxy access — automated solver pipeline") },
        { SubmitterNeverAccessedContainer, ( 30, "Submitter never personally opened the container; a teammate did") },
        { AccessIpMismatchAtSubmission,    ( 30, "Submitter's IP at submission time does not match any IP they used to access the container") },
    };

    // ── Fair-scoring tier model ───────────────────────────────────────────────
    // Each rule belongs to exactly one evidence tier. The tier — not the raw
    // weight sum — decides how much a signal can move a team's risk:
    //
    //   Hard       cross-team flag/session movement. Uncapped, forces the
    //              EVIDENCED band. The only thing that can rank a team as a
    //              cheater on its own.
    //   Strong     machine-speed / scanner behaviour. Tier subtotal ceiling 60
    //              (INVESTIGATE) — actionable but never "confirmed".
    //   Behavioral timing / similarity heuristics. Tier subtotal ceiling 25
    //              (WATCH) — low-confidence, never alarming alone.
    //   Context    network/identity correlation (IP, fingerprint, subnet…).
    //              Direct score is ALWAYS ZERO. Still recorded and shown; only
    //              *corroborates* hard evidence (see CorroborationUnit). This is
    //              the structural fix for innocent CGNAT/campus-NAT/VPN teams:
    //              no volume of IP events can make a team look like a cheater.
    //
    // Phase-1 note: NoDownload/NoContainer sit in Behavioral (not Strong) until
    // the challenge-prevalence suppression lands — they fire game-wide on
    // logging gaps / misconfigured challenges, so capping them at WATCH avoids
    // false orange flags.
    public static readonly Dictionary<string, SuspicionTier> Tiers = new()
    {
        // Hard — cross-team flag/session possession
        { StolenFlag, SuspicionTier.Hard },
        { CrossTeamContainerAccess, SuspicionTier.Hard },
        { WrongFlagLeakage, SuspicionTier.Hard },
        { TokenAbuse, SuspicionTier.Hard },
        { HoneypotCanaryFlag, SuspicionTier.Hard },

        // Strong — automation / scanner behaviour
        { AutomatedPattern, SuspicionTier.Strong },
        { HighWrongRate, SuspicionTier.Strong },
        { SolutionRelay, SuspicionTier.Strong },
        { HoneypotChain, SuspicionTier.Strong },
        { HoneypotProtocolHit, SuspicionTier.Strong },

        // Behavioral — timing / similarity heuristics
        { FastSolveOpen, SuspicionTier.Behavioral },
        { FastSolveDownload, SuspicionTier.Behavioral },
        { FastSolveContainer, SuspicionTier.Behavioral },
        { ZeroWrongAttempts, SuspicionTier.Behavioral },
        { Burst, SuspicionTier.Behavioral },
        { Hoarding, SuspicionTier.Behavioral },
        { SequenceSimilarity, SuspicionTier.Behavioral },
        { CollusionGroup, SuspicionTier.Behavioral },
        { AdaptiveFastSolve, SuspicionTier.Behavioral },
        { DirectedSolving, SuspicionTier.Behavioral },
        { FirstBloodAnomaly, SuspicionTier.Behavioral },
        { DelayedSolveSubmission, SuspicionTier.Behavioral },
        { InstantSubmitAfterAccess, SuspicionTier.Behavioral },
        { SubmitterNeverAccessedContainer, SuspicionTier.Behavioral },
        { HoneypotHit, SuspicionTier.Behavioral },
        { NoDownload, SuspicionTier.Behavioral },
        { NoContainer, SuspicionTier.Behavioral },

        // Context — network / identity correlation (NEVER scores on its own)
        { SharedIP, SuspicionTier.Context },
        { CrossTeamIP, SuspicionTier.Context },
        { UnknownIP, SuspicionTier.Context },
        { IpChurn, SuspicionTier.Context },
        { SubnetOverlap, SuspicionTier.Context },
        { SessionConcurrency, SuspicionTier.Context },
        { ClusteredRegistration, SuspicionTier.Context },
        { FingerprintChurn, SuspicionTier.Context },
        { SharedFingerprint, SuspicionTier.Context },
        { AccessIpMismatchAtSubmission, SuspicionTier.Context },
        { FlagEgress, SuspicionTier.Context },
    };

    /// Tier subtotal ceilings — a whole tier cannot contribute more than this.
    /// Hard is intentionally absent (uncapped at the tier level).
    public static readonly Dictionary<SuspicionTier, int> TierCeiling = new()
    {
        { SuspicionTier.Strong, 60 },
        { SuspicionTier.Behavioral, 25 },
        { SuspicionTier.Context, 0 },
    };

    /// Per-rule incident cap — how many distinct incidents of one rule may
    /// accumulate before further repeats stop adding score. Bounds Details-drift
    /// (each report refresh can mint a new row; the cap defuses it at read time).
    public static readonly Dictionary<string, int> MaxIncidents = new()
    {
        { StolenFlag, 10 }, { CrossTeamContainerAccess, 10 }, { WrongFlagLeakage, 10 },
        { TokenAbuse, 5 }, { HoneypotCanaryFlag, 3 },
        { AutomatedPattern, 3 }, { HighWrongRate, 3 }, { SolutionRelay, 2 },
        { HoneypotChain, 1 }, { HoneypotProtocolHit, 3 },
        { FastSolveOpen, 3 }, { FastSolveDownload, 3 }, { FastSolveContainer, 3 },
        { ZeroWrongAttempts, 3 }, { Burst, 3 }, { Hoarding, 3 }, { SequenceSimilarity, 3 },
        { CollusionGroup, 1 }, { AdaptiveFastSolve, 3 }, { DirectedSolving, 1 },
        { FirstBloodAnomaly, 4 }, { DelayedSolveSubmission, 5 }, { InstantSubmitAfterAccess, 3 },
        { SubmitterNeverAccessedContainer, 3 }, { HoneypotHit, 5 },
        { NoDownload, 3 }, { NoContainer, 3 },
    };

    /// Corroboration weight a context signal lends to *existing* hard evidence.
    /// Only ever applied when the team already has a Hard signal; the total
    /// corroboration bonus is itself capped at Hard/2 (see SuspicionScoring).
    public static int CorroborationUnit(string ruleCode) => ruleCode switch
    {
        SharedFingerprint => 20,
        CrossTeamIP => 10,
        SessionConcurrency => 10,
        _ => 5,
    };

    public static SuspicionTier GetTier(string ruleCode) =>
        Tiers.TryGetValue(ruleCode, out var tier) ? tier : SuspicionTier.Behavioral;

    public static int GetMaxIncidents(string ruleCode) =>
        MaxIncidents.TryGetValue(ruleCode, out var cap) ? cap : 3;

    // ── Display gate (separate from scoring) ───────────────────────────────────
    // These sets decide what is SHOWN to admins and PERSISTED — not how much a
    // signal is worth. Scoring is governed entirely by the evidence tiers above,
    // so a signal can be "displayed" (e.g. SharedIP, for transparency) yet score
    // exactly zero. Keeping the gate broad preserves the admin's full picture;
    // the tier model is what guarantees IP/identity can't inflate the score.

    /// Hard evidence — always persisted regardless of other signals.
    public static readonly HashSet<string> HardSignals = [
        StolenFlag, WrongFlagLeakage, NoContainer, NoDownload, TokenAbuse,
        HoneypotProtocolHit, HoneypotCanaryFlag, HoneypotChain, FlagEgress,
        CrossTeamContainerAccess
    ];

    /// Strong evidence — filed always; Soft signals unlock only when a Strong/Hard signal exists.
    public static readonly HashSet<string> StrongSignals = [
        ZeroWrongAttempts, SolutionRelay, HighWrongRate, AutomatedPattern,
        Burst, FingerprintChurn, SharedFingerprint, CollusionGroup,
        CrossTeamIP, SequenceSimilarity,
        FastSolveOpen, FastSolveDownload, FastSolveContainer, Hoarding, SharedIP,
        HoneypotHit,
        DelayedSolveSubmission, InstantSubmitAfterAccess
    ];

    /// True if the signal is "Soft" — suppressed from the report/persistence
    /// unless the team has a corroborating Strong/Hard signal in the same run.
    /// NOTE: this is a DISPLAY gate only; it does not affect the tiered score.
    public static bool IsSoft(string ruleCode) =>
        !HardSignals.Contains(ruleCode) && !StrongSignals.Contains(ruleCode);
}

/// Evidence tier — ordered by how strongly a signal implicates a team.
public enum SuspicionTier : byte
{
    /// Network/identity correlation. Direct score is always 0; corroborates only.
    Context = 0,
    /// Timing / similarity heuristics. Capped low; never alarming alone.
    Behavioral = 1,
    /// Automation / scanner behaviour. Actionable, capped below "confirmed".
    Strong = 2,
    /// Cross-team flag/session movement. Uncapped; forces the EVIDENCED band.
    Hard = 3,
}
