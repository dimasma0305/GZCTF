namespace GZCTF.Utils;

/// <summary>
/// Single source of truth for A&amp;D scoring so the live scoreboard, the
/// timeline, and the checker (which precomputes per-tick SLA credit) can't
/// drift. The model uses fixed per-event <b>pools</b> — an exchange rate between
/// the three pillars — so the balance is scale-invariant: it holds at any game
/// length, field size, or aggression level, with NO max/ceiling calibration
/// (the FAUST/ENOWARS approach).
///
/// <list type="bullet">
///   <item><b>Attack</b> — rarity pool. Each flag (one per victim·service·round)
///         is worth <see cref="AttackPool"/> points TOTAL, split equally among the
///         teams that stole it: a capturer of a flag taken by <c>k</c> teams gets
///         <c>AttackPool / k</c> (see <see cref="AttackShare"/>). Stealing a flag
///         only you found is worth the whole pool; a flag everyone steals is worth
///         a sliver each. Rewards exclusivity over brute-force volume and bounds
///         inflation (one flag injects at most one pool no matter how many pile on).
///         Because <c>k</c> depends on LATER captures, attack is computed at
///         scoreboard render, not at capture time — the capture response carries
///         only a provisional share.</item>
///   <item><b>Defense</b> — the mirror of attack: a team loses
///         <see cref="DefensePool"/> for each of its flags that leaked, counted once
///         per flag regardless of how many stole it (matching the one-pool-per-flag
///         attack payout). At equal pools the points attackers gained from you
///         exactly equal the points you lost — field-level zero-sum between offense
///         and defense.</item>
///   <item><b>SLA</b> — a per-tick <i>sum</i> (NOT a ratio):
///         <c>Σ tickCredit × SlaPerTickScale × sqrt(activeTeams)</c>, where a tick
///         scores 1.0 (Ok), 0.5 (Recovering = Ok right after a down tick), or 0.
///         <c>sqrt(teams)</c> keeps SLA on the same scale as the attack/defense
///         pools as the field grows. A sum (not ratio) means an Offline / Mumble /
///         InternalError tick simply earns 0 — it never drags a ratio down, which is
///         what made the old ratio model unfairly punish a <i>checker</i> fault.</item>
/// </list>
///
/// <para>Default pools are <b>1·1·1</b>. The rarity model makes a strong attacker's
/// total attack land naturally on the same scale as a perfect-uptime team's SLA, so
/// the board self-balances without tuning. Raise <see cref="AttackPool"/> for an
/// offense-led board; raise <see cref="DefensePool"/> to make getting popped bite
/// harder (it can push a heavily-farmed team's total negative).</para>
/// </summary>
public static class AdScoring
{
    /// <summary>Points one flag is worth in total, split among its capturers (rarity pool).</summary>
    public const double AttackPool = 1.0;

    /// <summary>Points a team loses per compromised flag (mirror of the attack pool).</summary>
    public const double DefensePool = 1.0;

    /// <summary>Per-tick SLA scale, applied on top of sqrt(teams).</summary>
    public const double SlaPerTickScale = 1.0;

    /// <summary>Full credit — service up + correct this tick.</summary>
    public const double SlaCreditOk = 1.0;

    /// <summary>Half credit — Ok this tick but down/mumble the previous tick (recovering).</summary>
    public const double SlaCreditRecovering = 0.5;

    /// <summary>No credit — Mumble / Offline / InternalError.</summary>
    public const double SlaCreditNone = 0.0;

    /// <summary>
    /// One capturer's share of a flag that <paramref name="capturers"/> teams stole in
    /// total: <c>AttackPool / capturers</c>. A flag only one team got pays that team the
    /// whole pool; a flag k teams got pays each <c>1/k</c>. Returns 0 for a non-positive
    /// count. Because the final capturer count isn't known until the flag expires,
    /// attack is computed at scoreboard render, not at capture time.
    /// </summary>
    public static double AttackShare(int capturers) =>
        capturers <= 0 ? 0.0 : AttackPool / capturers;

    /// <summary>
    /// Defense loss for a team: <see cref="DefensePool"/> per compromised flag — linear,
    /// counted once per flag (not per capture), mirroring the single pool a flag's
    /// stealers split. <paramref name="compromisedFlags"/> is the count of the team's
    /// distinct flags that leaked.
    /// </summary>
    public static double DefenseLoss(int compromisedFlags) =>
        DefensePool * compromisedFlags;

    /// <summary>
    /// Field-size weight (<c>sqrt(max(teams,1))</c>, the FAUST weighting that
    /// keeps SLA comparable to attack as the game grows). Folded into each
    /// tick's SLA credit WHEN THE CHECK LANDS, using that round's accepted-team
    /// count — so the stored credit is already field-scaled and a later roster
    /// change (accept/reject) can't retroactively rescale a team's whole SLA
    /// history (the bug an earlier render-time multiply had). The scoreboard
    /// then just SUMs the stored credit.
    /// </summary>
    public static double SlaFieldFactor(int activeTeams) => Math.Sqrt(Math.Max(1, activeTeams));

    /// <summary>
    /// SLA points from a sum of already-field-scaled per-tick credits (see
    /// <see cref="SlaFieldFactor" />). Just the per-tick scale — the team-count
    /// weight is baked into each stored credit, not applied here at render time.
    /// </summary>
    public static double SlaPoints(double creditSum) =>
        creditSum * SlaPerTickScale;

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
    /// <c>KothHoldPointsPerTick</c>, defaults to 1.0). NO team-count scaling:
    /// SLA scoring scales by <c>sqrt(teams)</c> to keep service-availability
    /// rewards comparable as the field grows, but KotH is a zero-sum race for
    /// one marker per challenge — only one team scores per tick, so the field
    /// size doesn't dilute anything. Clean integer per held tick matches the
    /// mental model ("hold one tick = +1") far better than the fractional
    /// <c>1×sqrt(3) ≈ 1.73</c> the scaled version produced.
    /// </summary>
    public static double KothHoldPoints(double holdPointsPerTick) => holdPointsPerTick;

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
        bool hasKing, AdCheckStatus status, double holdPointsPerTick,
        bool freshlyElected = false)
    {
        if (!hasKing)
            return (0.0, 0.0);
        if (status == AdCheckStatus.Ok)
            return (KothHoldPoints(holdPointsPerTick), 0.0);
        // Only a genuine box-down verdict the holder is responsible for is
        // penalized. InternalError is a CHECKER / infra fault (pruned image, no
        // network, container failed to start) — like the SLA TickCredit path, a
        // checker fault must never debit whoever happens to hold the hill at
        // fault time. A freshly-elected holder also gets one grace tick (they
        // inherited the previous holder's damage, not their fault yet).
        var holderAtFault = status is AdCheckStatus.Mumble or AdCheckStatus.Offline;
        return holderAtFault && !freshlyElected
            ? (0.0, KothBrokenHillPenalty)
            : (0.0, 0.0);
    }
}
