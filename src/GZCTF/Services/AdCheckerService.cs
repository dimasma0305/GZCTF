using System.Text;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Utils;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Services;

/// <summary>
/// Drives the A&amp;D checker loop: every <see cref="PollInterval"/>,
/// finds <c>(team, challenge)</c> pairs whose latest round has no
/// <see cref="AdCheckResult"/> row yet, dispatches them to
/// <see cref="AdCheckerExecutor"/> with bounded concurrency, persists
/// the outcomes.
///
/// <para>Separate from <see cref="AdRoundService"/> so round advance
/// stays sub-second — a checker run for 30 teams × M challenges adds
/// tens of seconds we don't want on the critical path. The polling
/// model is naturally idempotent on restart: a crashed mid-tick
/// resumes by finding the same un-checked services on the next pass.</para>
///
/// <para>Only the LATEST round per game is checked. Abandoned rounds
/// (e.g. operator restarted gzctf mid-game) stay un-scored on the SLA
/// term — re-running checks against a stale flag would either always
/// fail or, worse, succeed with the wrong flag if challenges leak past
/// flags through caches. Cleaner to let the current round race forward.</para>
///
/// <para>Cadence: 10s. Faster than container reconcile (15s) so we
/// catch services flipping back to Ok quickly after a player patches;
/// slower than the round scheduler (5s) so we don't hammer the DB on
/// the rounds in between.</para>
///
/// <para>K8s deployments: silently no-op (Docker provider not
/// registered). Same posture as <see cref="AdRoundService"/>'s flag
/// inject path.</para>
/// </summary>
public sealed class AdCheckerService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    Cache.CacheHelper cacheHelper,
    ILogger<AdCheckerService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private int MaxParallel => int.TryParse(configuration["Ad:Checker:MaxParallel"], out var n) && n > 0 ? n : 10;

    /// <summary>Attempts per check before recording a verdict. Retries absorb
    /// transient blips / unlucky jittered timing so one dropped packet doesn't
    /// cost a team a full tick of SLA. Stops early on the first Ok.</summary>
    private const int MaxCheckAttempts = 3;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(1500);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.SystemLog(
            $"AdCheckerService started; tick every {PollInterval.TotalSeconds}s, max parallel = {MaxParallel}",
            TaskStatus.Pending, LogLevel.Information);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickOnceAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogErrorMessage(e, "AdCheckerService tick failed; will retry");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickOnceAsync(CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Provider-agnostic: the check runner is registered per container
        // provider (Docker → AdCheckerExecutor, K8s → K8sAdCheckRunner). If no
        // container provider is registered at all, there's nothing to check.
        var runner = scope.ServiceProvider.GetService<IAdCheckRunner>();
        if (runner is null)
        {
            logger.LogDebug("AdChecker tick: no check runner registered, skipping");
            return;
        }

        var now = DateTimeOffset.UtcNow;

        var activeGameIds = await db.Games
            .Where(g => g.StartTimeUtc <= now && now <= g.EndTimeUtc && !g.AdScoringPaused)
            // KotH challenges share the round/checker plumbing; CheckKothChallengesAsync
            // below handles their probes after the A&D pass.
            .Where(g => g.Challenges.Any(c => (c.Type == ChallengeType.AttackDefense
                                            || c.Type == ChallengeType.KingOfTheHill) && c.IsEnabled))
            .Select(g => g.Id)
            .ToListAsync(token);

        logger.LogDebug("AdChecker tick: {N} active A&D games", activeGameIds.Count);

        foreach (var gameId in activeGameIds)
            await CheckGameAsync(db, runner, gameId, token);
    }

    /// <summary>
    /// When this service's getflag check becomes due within the current tick:
    /// <c>StartedAt + grace + jitter</c>, where jitter is a stable
    /// per-(service, round) offset inside <c>AdGetflagWindowFraction</c> of the
    /// tick. Grace is capped at half the tick, and the whole schedule is clamped
    /// to leave one poll interval before the round ends so the check still fires
    /// before the round advances. Re-rolls each round.
    /// </summary>
    internal static DateTimeOffset GetflagDueAt(
        AdTeamService ts, AdRound round, double tickSeconds, double pollSeconds, int graceSeconds, double getflagFraction)
    {
        var grace = Math.Max(0, graceSeconds);
        var getFrac = Math.Clamp(getflagFraction, 0.0, 1.0);
        var graceSec = Math.Min(grace, tickSeconds * 0.5);
        var maxJitter = Math.Max(0.0, tickSeconds - graceSec - pollSeconds);
        var jitterSec = Math.Min(getFrac * tickSeconds, maxJitter);
        return round.StartedAt.AddSeconds(graceSec + StableJitterFraction(ts.Id, round.Id) * jitterSec);
    }

    /// <summary>Deterministic [0,1) per (service, round): stable across polls and
    /// process restarts (so the due time never moves mid-tick) but fresh every
    /// round (so the jitter pattern can't be fingerprinted).</summary>
    internal static double StableJitterFraction(int serviceId, int roundId)
    {
        unchecked
        {
            var h = (uint)(serviceId * 73856093) ^ (uint)(roundId * 19349663);
            return h % 100000u / 100000.0;
        }
    }

    /// <summary>Run the checker, retrying up to <see cref="MaxCheckAttempts"/>
    /// times on any non-Ok verdict (returns on the first Ok). Absorbs transient
    /// network blips and unlucky jittered timing so they don't zero a team's
    /// SLA for the tick.</summary>
    private static async Task<AdCheckOutcome> RunWithRetryAsync(
        IAdCheckRunner runner, AdTeamService ts, AdRound round, GameChallenge challenge,
        string? flag, CancellationToken token)
    {
        AdCheckOutcome outcome = null!;
        for (var attempt = 1; attempt <= MaxCheckAttempts; attempt++)
        {
            outcome = await runner.RunAsync(ts, round, challenge, flag, token);
            if (outcome.Status == AdCheckStatus.Ok)
                return outcome;
            if (attempt < MaxCheckAttempts)
                await Task.Delay(RetryDelay, token);
        }
        return outcome;
    }

    private async Task CheckGameAsync(
        AppDbContext db, IAdCheckRunner runner, int gameId, CancellationToken token)
    {
        // Latest round for this game. If null, we're still in warmup —
        // no flags, no checks.
        var latest = await db.AdRounds
            .Where(r => r.GameId == gameId)
            .OrderByDescending(r => r.Number)
            .FirstOrDefaultAsync(token);
        if (latest is null) return;

        // Pull (service, challenge) pairs that:
        //   - belong to an accepted participation in this game
        //   - have a live container
        //   - whose challenge is an enabled A&D challenge
        //   - have NO AdCheckResult yet for this latest round
        // The NOT EXISTS keeps the scheduler idempotent across restarts.
        var pending = await db.AdTeamServices
            .Where(ts => ts.Participation.GameId == gameId
                && ts.Participation.Status == ParticipationStatus.Accepted
                && ts.ContainerId != null
                && ts.Challenge.Type == ChallengeType.AttackDefense
                && ts.Challenge.IsEnabled
                && !db.AdCheckResults.Any(cr =>
                    cr.AdRoundId == latest.Id && cr.AdTeamServiceId == ts.Id))
            .Include(ts => ts.Container)
            .Include(ts => ts.Challenge)
            .ToListAsync(token);

        logger.LogDebug("AdChecker: game={Gid} round={Round} pending={N}",
            gameId, latest.Number, pending.Count);

        if (pending.Count == 0)
        {
            // No A&D services this game, but it may still have KotH hills to check.
            await CheckKothChallengesAsync(db, runner, gameId, latest, token);
            await cacheHelper.FlushAdScoreboardCache(gameId, token);
            return;
        }

        // Getflag jitter + grace: each service's check fires at a per-(service,
        // round) randomized offset inside the tick — never before
        // AdMinGracePeriodSeconds (lets the service settle after putflag) and
        // within AdGetflagWindowFraction of the tick after that. Re-rolled each
        // round so teams can't predict the SLA check and hide their box only
        // during it. Services not yet due this poll are picked up on a later one.
        var nowTs = DateTimeOffset.UtcNow;
        var tickSeconds = Math.Max(1.0, (latest.EndsAt - latest.StartedAt).TotalSeconds);
        var pollSeconds = PollInterval.TotalSeconds;
        // Getflag jitter window + min grace are event-wide (on Game): the tick
        // they're fractions of is shared across all A&D services in the game.
        var timing = await db.Games
            .Where(g => g.Id == gameId)
            .Select(g => new { g.AdGetflagWindowFraction, g.AdMinGracePeriodSeconds })
            .FirstOrDefaultAsync(token);
        var graceSeconds = timing?.AdMinGracePeriodSeconds ?? 3;
        var getflagFraction = timing?.AdGetflagWindowFraction ?? 0.5;
        var due = pending.Where(ts =>
            nowTs >= GetflagDueAt(ts, latest, tickSeconds, pollSeconds, graceSeconds, getflagFraction)).ToList();
        if (due.Count == 0) return;

        // Custom-image checkers run one-per-check (enochecker one-target contract);
        // the built-in TCP probe runs as a single batch (in-process on Docker, one
        // prober pod on K8s) instead of a container/pod per service.
        var customDue = due.Where(ts => !string.IsNullOrWhiteSpace(ts.Challenge.AdCheckerImage)).ToList();
        var builtinDue = due.Where(ts => string.IsNullOrWhiteSpace(ts.Challenge.AdCheckerImage)).ToList();

        if (customDue.Count > 0)
        {
            // Pre-fetch the planted flag for each (team, challenge) so the
            // executor doesn't need DB access. Round N's flag is what the
            // checker should pass via GZCTF_FLAG.
            var serviceIds = customDue.Select(ts => ts.Id).ToList();
            var flagByService = await db.AdFlags
                .Where(f => f.AdRoundId == latest.Id && serviceIds.Contains(f.AdTeamServiceId))
                .Select(f => new { f.AdTeamServiceId, f.Flag })
                .ToDictionaryAsync(f => f.AdTeamServiceId, f => f.Flag, token);

            using var gate = new SemaphoreSlim(MaxParallel, MaxParallel);
            var tasks = new List<Task>(customDue.Count);

            foreach (var ts in customDue)
            {
                await gate.WaitAsync(token);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        flagByService.TryGetValue(ts.Id, out var flag);
                        var outcome = await RunWithRetryAsync(runner, ts, latest, ts.Challenge, flag, token);
                        await PersistOutcomeAsync(scopeFactory, ts.Id, latest.Id, outcome, token);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        logger.LogWarning(e,
                            "AdChecker: dispatch failed for service={Sid} round={Round}", ts.Id, latest.Number);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, token));
            }

            await Task.WhenAll(tasks);
        }

        if (builtinDue.Count > 0)
            await RunBuiltinBatchAsync(runner, builtinDue, latest, token);

        // KotH hills in this game (independent of the A&D per-team checks above).
        await CheckKothChallengesAsync(db, runner, gameId, latest, token);

        // New verdicts persisted → SLA changed; refresh the cached board
        // (background regen, de-bounced by CacheMaker so frequent ticks collapse).
        await cacheHelper.FlushAdScoreboardCache(gameId, token);
    }

    /// <summary>
    /// King of the Hill per-tick evaluation for every hill in the game: a functional
    /// probe (reuses the A&amp;D checker) + an external read of the <c>/koth/king</c>
    /// marker matched against this round's issued tokens → the controller. Writes one
    /// <see cref="KothControlResult"/> per (challenge, round): functional king earns
    /// hold points; a king on a broken hill eats the flat penalty; no valid king → 0.
    ///
    /// <para><b>Attribution caveat (known limitation):</b> the controller is whichever
    /// team's token matches the marker bytes — we cannot tell who actually wrote
    /// them. A team that observes another team's token in <c>/koth/king</c> (any
    /// team that can read the shared hill can) and replays it credits the original
    /// bearer, not the replayer. The bearer is still the legitimate scorer (no
    /// score transfer happens), but the replayer can force a specific victim to
    /// stay "controller" — which used to chain into a permanent leader-cooldown.
    /// Mitigations in place:
    /// <list type="bullet">
    ///   <item>Leader cooldown uses a recent-window definition (last refresh interval
    ///         only) — see <c>ApplyKothLeaderCooldownAsync</c> — so force-feeding a
    ///         victim controller status rotates the cooldown to them only briefly,
    ///         not permanently.</item>
    ///   <item>The matched token id + controller are logged at Information level on
    ///         every persisted result, giving organizers a post-game audit trail.</item>
    /// </list>
    /// A full attribution fix would require an out-of-band write-source channel
    /// (challenge-image-level source-IP logging, or a packet-capture sidecar on the
    /// hill bridge). Tracked as future work; not implementable inside the checker.</para>
    /// </summary>
    private async Task CheckKothChallengesAsync(
        AppDbContext db, IAdCheckRunner runner, int gameId, AdRound latest, CancellationToken token)
    {
        var kothChallenges = await db.GameChallenges
            .Where(c => c.GameId == gameId && c.Type == ChallengeType.KingOfTheHill && c.IsEnabled)
            .ToListAsync(token);
        if (kothChallenges.Count == 0)
            return;

        var game = await db.Games
            .Where(g => g.Id == gameId)
            .Select(g => new { g.KothHoldPointsPerTick, g.AdMinGracePeriodSeconds, g.AdGetflagWindowFraction })
            .FirstOrDefaultAsync(token);
        var holdPerTick = game?.KothHoldPointsPerTick ?? 1.0;
        var graceSeconds = game?.AdMinGracePeriodSeconds ?? 3;
        var getFrac = game?.AdGetflagWindowFraction ?? 0.5;

        var activeTeams = await db.Participations
            .CountAsync(p => p.GameId == gameId && p.Status == ParticipationStatus.Accepted, token);

        var tickSeconds = Math.Max(1.0, (latest.EndsAt - latest.StartedAt).TotalSeconds);
        var pollSeconds = PollInterval.TotalSeconds;
        var now = DateTimeOffset.UtcNow;

        foreach (var challenge in kothChallenges)
        {
            // One result per hill per round (idempotent across the 10s cadence).
            if (await db.KothControlResults.AnyAsync(
                    r => r.ChallengeId == challenge.Id && r.AdRoundId == latest.Id, token))
                continue;

            // Jitter the check time within the tick, same as A&D getflag — keyed on
            // the challenge id so teams can't predict when the marker is sampled.
            var due = GetflagDueAt(new AdTeamService { Id = challenge.Id }, latest, tickSeconds, pollSeconds, graceSeconds, getFrac);
            if (now < due)
                continue;

            // Take the same per-challenge lock the reconciler uses on the shared
            // hill (AdContainerManager.LockFor(0, challenge.Id)). Without this, a
            // 5-tick refresh that destroys+relaunches the hill mid-probe races us:
            // we read /koth/king from the just-launched (empty) container and
            // record "no controller" for a round where the marker had been set
            // legitimately. The reconciler also defers its refresh trigger to the
            // round AFTER the boundary so that the boundary round's result is
            // persisted before any wipe — these two changes work together.
            await using var checkScope = scopeFactory.CreateAsyncScope();
            var mgr = checkScope.ServiceProvider.GetRequiredService<AdContainerManager>();
            await mgr.WithKothChallengeLockAsync(challenge.Id, async () =>
            {
                // Re-check inside the lock — the reconciler may have just relaunched.
                var target = await db.KothTargets
                    .Include(t => t.Container)
                    .FirstOrDefaultAsync(t => t.GameId == gameId && t.ChallengeId == challenge.Id, token);

                if (target?.Container is null || string.IsNullOrEmpty(target.Container.IP))
                {
                    await PersistKothResultAsync(scopeFactory, gameId, challenge.Id, latest.Id,
                        null, AdCheckStatus.Offline, 0, 0, "hill not running", token);
                    return;
                }

                // Synthesize a transient service wrapping the shared hill so we can
                // reuse the A&D functional checker + the container file-read unchanged.
                var hillTs = new AdTeamService
                {
                    Id = 0,
                    ParticipationId = 0,
                    ChallengeId = challenge.Id,
                    ContainerId = target.ContainerId,
                    Container = target.Container
                };

                var outcome = await RunWithRetryAsync(runner, hillTs, latest, challenge, null, token);

                int? controller = null;
                int? matchedTokenId = null;
                var blob = await mgr.ReadCurrentFileBytesAsync(checkScope.ServiceProvider, hillTs, "/koth/king", token);
                if (blob is { } b && b.Data.Length > 0)
                {
                    var marker = Encoding.UTF8.GetString(b.Data).Trim();
                    if (marker.Length > 0)
                    {
                        var match = await db.KothTokens
                            .Where(k => k.ChallengeId == challenge.Id && k.RoundNumber == latest.Number && k.Token == marker)
                            .Select(k => new { k.Id, k.ParticipationId })
                            .FirstOrDefaultAsync(token);
                        if (match is not null)
                        {
                            controller = match.ParticipationId;
                            matchedTokenId = match.Id;
                        }
                    }
                }

                // Audit trail for the H1 attribution caveat documented above —
                // record (round, challenge, controller, matched token id) so
                // organizers can investigate suspected replay patterns post-game
                // (e.g. the same team being credited every tick without their
                // VPN showing recent handshakes — visible by cross-referencing
                // AdVpnPeers + wireguard logs).
                logger.SystemLog(
                    $"KotH check: round={latest.Number} chal={challenge.Id} controller={controller?.ToString() ?? "none"} tokenId={matchedTokenId?.ToString() ?? "-"} status={outcome.Status}",
                    TaskStatus.Success, LogLevel.Information);

                var (hold, penalty) = AdScoring.KothTickDelta(controller is not null, outcome.Status, holdPerTick, activeTeams);
                await PersistKothResultAsync(scopeFactory, gameId, challenge.Id, latest.Id,
                    controller, outcome.Status, hold, penalty, outcome.ErrorMessage, token);
            }, token);
        }
    }

    /// <summary>Insert one <see cref="KothControlResult"/> (idempotent on (challenge, round)).</summary>
    private static async Task PersistKothResultAsync(
        IServiceScopeFactory scopeFactory, int gameId, int challengeId, int adRoundId,
        int? controller, AdCheckStatus status, double hold, double penalty, string? error, CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (await db.KothControlResults.AnyAsync(r => r.ChallengeId == challengeId && r.AdRoundId == adRoundId, token))
            return;

        await db.KothControlResults.AddAsync(new KothControlResult
        {
            GameId = gameId,
            ChallengeId = challengeId,
            AdRoundId = adRoundId,
            ControllingParticipationId = controller,
            Status = status,
            HoldCredit = hold,
            Penalty = penalty,
            ErrorMessage = error,
            CheckedAt = DateTimeOffset.UtcNow
        }, token);

        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException) { /* concurrent tick won the (challenge, round) race — fine */ }
    }

    /// <summary>
    /// Run the built-in TCP probe for a batch of services in one shot
    /// (<see cref="IAdCheckRunner.RunBuiltinBatchAsync"/>), retrying the non-Ok
    /// subset to absorb transient blips (same intent as
    /// <see cref="RunWithRetryAsync"/>), then persist a verdict per service.
    /// </summary>
    private async Task RunBuiltinBatchAsync(
        IAdCheckRunner runner, List<AdTeamService> builtinDue, AdRound round, CancellationToken token)
    {
        // Services with no live container IP can't be probed → Offline.
        var targets = new List<AdBuiltinTarget>(builtinDue.Count);
        foreach (var ts in builtinDue)
            if (ts.Container?.IP is { Length: > 0 } ip)
                targets.Add(new AdBuiltinTarget(ts.Id, ip, ts.Challenge.ExposePort ?? 80));

        var ok = new HashSet<int>();
        var remaining = targets;
        for (var attempt = 1; attempt <= MaxCheckAttempts && remaining.Count > 0; attempt++)
        {
            var res = await runner.RunBuiltinBatchAsync(remaining, token);
            var retry = new List<AdBuiltinTarget>();
            foreach (var t in remaining)
            {
                if (res.GetValueOrDefault(t.ServiceId, AdCheckStatus.Offline) == AdCheckStatus.Ok)
                    ok.Add(t.ServiceId);
                else
                    retry.Add(t);
            }
            remaining = retry;
            if (remaining.Count > 0 && attempt < MaxCheckAttempts)
                await Task.Delay(RetryDelay, token);
        }

        foreach (var ts in builtinDue)
        {
            var status = ok.Contains(ts.Id) ? AdCheckStatus.Ok : AdCheckStatus.Offline;
            var outcome = new AdCheckOutcome(status, status == AdCheckStatus.Ok ? null : "tcp probe failed", null);
            await PersistOutcomeAsync(scopeFactory, ts.Id, round.Id, outcome, token);
        }
    }

    /// <summary>
    /// Insert one <see cref="AdCheckResult"/> row in its own scoped DB
    /// context so parallel writers don't contend on a shared change
    /// tracker.
    /// </summary>
    private static async Task PersistOutcomeAsync(
        IServiceScopeFactory scopeFactory,
        int adTeamServiceId,
        int adRoundId,
        AdCheckOutcome outcome,
        CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Race-safe: another instance/tick may have already inserted a
        // row for this (service, round) pair. Re-check before insert.
        var exists = await db.AdCheckResults
            .AnyAsync(cr => cr.AdRoundId == adRoundId && cr.AdTeamServiceId == adTeamServiceId, token);
        if (exists) return;

        // Per-tick SLA credit is computed once, here, from this service's
        // PREVIOUS verdict — "Ok right after a down tick" is the recovering
        // (half-credit) case. The scoreboard then just SUMs SlaCredit.
        var prevStatus = await db.AdCheckResults
            .Where(cr => cr.AdTeamServiceId == adTeamServiceId && cr.AdRoundId < adRoundId)
            .OrderByDescending(cr => cr.AdRoundId)
            .Select(cr => (AdCheckStatus?)cr.Status)
            .FirstOrDefaultAsync(token);

        var credit = AdScoring.TickCredit(outcome.Status, prevStatus);
        await db.AdCheckResults.AddAsync(new AdCheckResult
        {
            AdTeamServiceId = adTeamServiceId,
            AdRoundId = adRoundId,
            Status = outcome.Status,
            SlaCredit = credit,
            ErrorMessage = outcome.ErrorMessage,
            SourceIp = outcome.SourceIp,
            CheckedAt = DateTimeOffset.UtcNow
        }, token);

        // Maintain the per-service running SLA total in the SAME transaction as
        // the insert — so the live scoreboard reads it (O(teams)) instead of
        // summing all check rows, and it can't drift from the row set.
        var svc = await db.AdTeamServices.FirstOrDefaultAsync(s => s.Id == adTeamServiceId, token);
        if (svc is not null)
            svc.SlaCreditTotal += credit;

        try
        {
            await db.SaveChangesAsync(token);
        }
        catch (DbUpdateException)
        {
            // Unique-ish conflict from another tick winning the race —
            // safe to ignore. (The schema doesn't have a unique index
            // on (round, service) yet, but the re-check above + the
            // 10s cadence makes duplicates a non-issue in practice.)
        }
    }
}
