namespace GZCTF.Utils;

/// <summary>
/// Single source of truth for A&amp;D scoring so the live scoreboard, the
/// timeline, and the checker (which precomputes per-tick SLA credit) can't
/// drift. The model is synthesized from the two most-used open A&amp;D
/// platforms — FAUST CTF and ENOWARS/EnoEngine:
///
/// <list type="bullet">
///   <item><b>Attack</b> — first-blood weighted:
///         <c>AttackBasePoints / sqrt(capture_order)</c>. The 1st team to
///         steal a given flag gets full points, later capturers get less.
///         (Rarity/speed weighting, same spirit as FAUST's
///         <c>1/captures</c> and Eno's decreasing jeopardy.)</item>
///   <item><b>Defense</b> — penalty
///         <c>DefensePenaltyScale × timesCaptured^0.75</c>. The 0.75
///         exponent is FAUST's exact value: early losses hurt more than
///         later ones, sub-linearly.</item>
///   <item><b>SLA</b> — a per-tick <i>sum</i> (NOT a ratio):
///         <c>Σ tickCredit × SlaPerTickScale × sqrt(activeTeams)</c>, where
///         a tick scores 1.0 (Ok), 0.5 (Recovering = Ok right after a down
///         tick, FAUST's transitional credit), or 0. <c>sqrt(teams)</c>
///         scales SLA with field size so it stays comparable to attack as
///         the game grows (FAUST). A sum model means an Offline / Mumble /
///         InternalError tick simply earns 0 — it never drags down a ratio,
///         which is what made the old ratio model unfairly penalize teams
///         for a <i>checker</i> fault (InternalError) they didn't cause.</item>
/// </list>
/// </summary>
public static class AdScoring
{
    /// <summary>Per-flag attack base, divided by sqrt of 1-indexed capture order.</summary>
    public const double AttackBasePoints = 10.0;

    /// <summary>Multiplier on the defense-loss penalty curve.</summary>
    public const double DefensePenaltyScale = 2.0;

    /// <summary>Sub-linear defense exponent (FAUST uses 0.75).</summary>
    public const double DefenseExponent = 0.75;

    /// <summary>Per-tick SLA scale, applied on top of sqrt(teams).</summary>
    public const double SlaPerTickScale = 1.0;

    /// <summary>Full credit — service up + correct this tick.</summary>
    public const double SlaCreditOk = 1.0;

    /// <summary>Half credit — Ok this tick but down/mumble the previous tick (recovering).</summary>
    public const double SlaCreditRecovering = 0.5;

    /// <summary>No credit — Mumble / Offline / InternalError.</summary>
    public const double SlaCreditNone = 0.0;

    /// <summary>Attack points for the <paramref name="priorCapturers"/>-th capturer (0-indexed).</summary>
    public static double AttackPoints(int priorCapturers) =>
        AttackBasePoints / Math.Sqrt(priorCapturers + 1);

    /// <summary>Defense loss for a team whose flags were captured <paramref name="timesCaptured"/> times.</summary>
    public static double DefenseLoss(int timesCaptured) =>
        Math.Pow(timesCaptured, DefenseExponent) * DefensePenaltyScale;

    /// <summary>
    /// SLA points from a sum of per-tick credits, scaled by field size.
    /// <c>sqrt(max(teams,1))</c> keeps SLA comparable to attack as the game
    /// grows — the FAUST weighting.
    /// </summary>
    public static double SlaPoints(double creditSum, int activeTeams) =>
        creditSum * SlaPerTickScale * Math.Sqrt(Math.Max(1, activeTeams));

    /// <summary>
    /// Per-tick SLA credit for a fresh verdict given the service's previous
    /// verdict. Ok right after an Offline/Mumble tick is "recovering"
    /// (half credit); a clean Ok is full; everything else earns nothing.
    /// Computed once when the check lands (see AdCheckerService) and stored
    /// on the row, so the scoreboard just SUMs it.
    /// </summary>
    public static double TickCredit(AdCheckStatus current, AdCheckStatus? previous) =>
        current switch
        {
            AdCheckStatus.Ok when previous is AdCheckStatus.Offline or AdCheckStatus.Mumble
                => SlaCreditRecovering,
            AdCheckStatus.Ok => SlaCreditOk,
            _ => SlaCreditNone
        };

    /// <summary>Flat penalty debited from a team that holds a broken (Mumble/Offline) hill.</summary>
    public const double KothBrokenHillPenalty = 1.0;

    /// <summary>
    /// King of the Hill — hold points for the controlling team this tick. Flat
    /// <paramref name="holdPointsPerTick"/> (the game's
    /// <c>KothHoldPointsPerTick</c>, defaults to 1.0) — NO team-count scaling.
    /// SLA scoring scales by <c>sqrt(teams)</c> to keep service-availability
    /// rewards comparable as the field grows, but KotH is a zero-sum race for
    /// one marker per challenge: one team holds it per tick, and a clean
    /// integer per held tick matches the mental model ("hold one tick = +1")
    /// far better than the fractional <c>1×sqrt(3) ≈ 1.73</c> the scaled
    /// version produced. <paramref name="activeTeams"/> kept in the signature
    /// for symmetry with the rest of AdScoring + in case a future variant
    /// wants to opt back in to scaling.
    /// </summary>
    public static double KothHoldPoints(double holdPointsPerTick, int activeTeams) =>
        holdPointsPerTick;

    /// <summary>
    /// King of the Hill per-tick score delta for whoever holds the marker, returned
    /// as <c>(HoldCredit, Penalty)</c>. Functional hill + king → (+hold, 0); a king
    /// holding a broken (Mumble/Offline/InternalError) hill → (0, penalty) — you
    /// broke the box you hold; no king → (0, 0). The scoreboard sums HoldCredit −
    /// Penalty per team.
    ///
    /// <para><paramref name="freshlyElected"/> grants a one-tick grace on the broken-hill
    /// penalty: a team that just took over the marker (different controller from
    /// the previous tick) didn't have time to fix damage left by the previous
    /// holder, so they get (0, 0) on a broken hill instead of (0, penalty). Once
    /// they hold the hill into a second tick (<c>freshlyElected=false</c>) they're
    /// on the hook normally.</para>
    /// </summary>
    public static (double HoldCredit, double Penalty) KothTickDelta(
        bool hasKing, AdCheckStatus status, double holdPointsPerTick, int activeTeams,
        bool freshlyElected = false) =>
        !hasKing
            ? (0.0, 0.0)
            : status == AdCheckStatus.Ok
                ? (KothHoldPoints(holdPointsPerTick, activeTeams), 0.0)
                : freshlyElected
                    ? (0.0, 0.0) // grace tick — broken when they took it, not their fault yet
                    : (0.0, KothBrokenHillPenalty);
}
