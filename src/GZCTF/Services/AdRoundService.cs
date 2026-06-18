using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Services.Container.Provider;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Services;

/// <summary>
/// Shared round-advance + flag-plant logic for A&amp;D games. Used by both:
/// <list type="bullet">
///   <item>The admin "Advance round" button (operator-driven force advance)</item>
///   <item>The <see cref="AdRoundScheduler"/> background service (auto
///         tick-end advance after the game's warmup window expires)</item>
/// </list>
/// Keeping a single code path means the auto-advancer and the manual button
/// can never diverge on scoring semantics — same flag format, same plant-all-
/// teams behavior, same tick-window math.
/// </summary>
public sealed class AdRoundService(
    AppDbContext db,
    IServiceProvider serviceProvider,
    AdFlagMountService flagMount,
    ILogger<AdRoundService> logger)
{
    private const int FlagRandomBytes = 24;
    private const string FlagFilePath = "/flag";

    public sealed record Result(AdRound Round, int FlagsPlanted);

    /// <summary>
    /// Insert the next AdRound for the game + plant a fresh flag for every
    /// (team, enabled A&amp;D challenge) with a live container. Returns the
    /// new round + the flag-plant count, or null if the game has no enabled
    /// A&amp;D challenges (which the caller treats as "nothing to do").
    /// </summary>
    public async Task<Result?> AdvanceAsync(int gameId, CancellationToken token)
    {
        // Both A&D and KotH run on this round engine. A&D plants a per-team flag
        // into each team's own container; KotH instead mints a per-team rotating
        // token the team must itself plant into the shared hill's marker.
        var engineChallenges = await db.GameChallenges
            .Where(c => c.GameId == gameId && c.IsEnabled
                && (c.Type == ChallengeType.AttackDefense || c.Type == ChallengeType.KingOfTheHill))
            .Select(c => new { c.Id, c.Type })
            .ToListAsync(token);

        if (engineChallenges.Count == 0)
            return null;

        var kothChallengeIds = engineChallenges
            .Where(c => c.Type == ChallengeType.KingOfTheHill).Select(c => c.Id).ToList();

        var prev = await db.AdRounds
            .Where(r => r.GameId == gameId)
            .OrderByDescending(r => r.Number)
            .FirstOrDefaultAsync(token);

        var nextNumber = (prev?.Number ?? 0) + 1;

        // Tick length is an event-wide knob on the game — a round spans the
        // whole game, so every A&D service shares one tick window. KothRefreshTicks
        // is the hill reset cadence; the KotH token rotates on the same boundary.
        var gameKnobs = await db.Games
            .Where(g => g.Id == gameId)
            .Select(g => new { g.AdTickSeconds, g.KothRefreshTicks })
            .FirstOrDefaultAsync(token);
        var tickSeconds = gameKnobs?.AdTickSeconds ?? 60;
        var refreshTicks = Math.Max(1, gameKnobs?.KothRefreshTicks ?? 5);

        var now = DateTimeOffset.UtcNow;
        var round = new AdRound
        {
            GameId = gameId,
            Number = nextNumber,
            StartedAt = now,
            EndsAt = now.AddSeconds(tickSeconds)
        };

        var services = await db.AdTeamServices
            .Where(ts => ts.Participation.GameId == gameId)
            .Include(ts => ts.Container)
            .ToListAsync(token);

        // (service, flag) pairs we need to push into containers after the
        // DB save commits. Collected up-front so the exec calls don't race
        // with the row inserts.
        var toInject = new List<(AdTeamService Service, string Flag)>();

        // Single transaction wrapping the round insert + flag inserts + KotH
        // token inserts: under Postgres read-committed isolation, other
        // connections (AdCheckerService, AdContainerManager, the AdGameController
        // endpoints) don't see the new round until the transaction commits, so
        // they can't observe a window where the round is live but its tokens /
        // flags aren't yet — which previously let a checker tick fire against
        // a freshly-advanced round whose KothTokens hadn't been minted, recording
        // "no controller" for what should have been a normal tick. Docker exec
        // (best-effort flag plant) deliberately stays OUTSIDE the transaction —
        // we don't want a slow container to block the round from going visible.
        await using var tx = await db.Database.BeginTransactionAsync(token);

        await db.AdRounds.AddAsync(round, token);
        await db.SaveChangesAsync(token); // populates round.Id for AdFlags FK

        foreach (var ts in services)
        {
            var bytes = new byte[FlagRandomBytes];
            RandomNumberGenerator.Fill(bytes);
            var payload = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '_').Replace('/', '-');
            var flag = $"flag{{{payload}}}";

            await db.AdFlags.AddAsync(new AdFlag
            {
                AdRoundId = round.Id,
                AdTeamServiceId = ts.Id,
                PlantedAtRound = nextNumber,
                Flag = flag,
                PlantedAt = now
            }, token);
            toInject.Add((ts, flag));
        }

        // KotH: each accepted team's control token. The platform does NOT plant it —
        // the team writes it into /koth/king once they have a foothold; the king-check
        // reads the marker and matches it back.
        //
        // The token is GAME-WIDE and STABLE for a whole refresh window: ONE token per
        // team covers EVERY hill in the game, and it rotates to a fresh value only on
        // the window boundary (every refreshTicks ticks, when the hills reset to base
        // and the markers are wiped). So a team plants the same string on whichever
        // hills it captures, once per window, instead of juggling a different token per
        // hill or re-planting every tick.
        //
        // Mint exactly one token per (team) ONLY on a refresh-window boundary
        // (rounds 1, 1+refreshTicks, 1+2·refreshTicks, …) — the same boundary the hills
        // reset on. The token then stays the window's stable value: the king-check and
        // the token endpoint resolve it by the window's ANCHOR round, so the intervening
        // ticks reuse it without re-minting. (A per-tick mint that reused a value would
        // also violate the unique Token index.)
        var kothTokensMinted = 0;
        if (kothChallengeIds.Count > 0 && KothWindow.IsMintBoundary(nextNumber, refreshTicks))
        {
            var participationIds = await db.Participations
                .Where(p => p.GameId == gameId && p.Status == ParticipationStatus.Accepted)
                .Select(p => p.Id)
                .ToListAsync(token);

            foreach (var pid in participationIds)
            {
                var tbytes = new byte[FlagRandomBytes];
                RandomNumberGenerator.Fill(tbytes);
                var tpayload = Convert.ToBase64String(tbytes).TrimEnd('=').Replace('+', '_').Replace('/', '-');
                await db.KothTokens.AddAsync(new KothToken
                {
                    ParticipationId = pid,
                    RoundNumber = nextNumber,
                    AdRoundId = round.Id, // FK — cascade-deletes if the round is rolled back
                    Token = $"koth_{tpayload}",
                    IssuedAt = now
                }, token);
            }

            kothTokensMinted = participationIds.Count;
        }

        // Commit flags + KotH tokens together with the round. After this point the
        // round (and its tokens) are visible to checkers / queries — before this
        // point they're invisible (other connections see nothing because of the
        // transaction).
        await db.SaveChangesAsync(token);
        await tx.CommitAsync(token);

        // Plant each flag. Best-effort, OUTSIDE the transaction — a slow container
        // can't block the round from going visible, and a failure only loses one
        // tick of attackability for that team; the next round retries.
        var docker = serviceProvider
            .GetService<IContainerProvider<DockerClient, DockerMetadata>>()
            ?.GetProvider();
        int injected = 0;

        // Preferred path: rewrite the read-only host-backed file in place. The
        // container's :ro bind mount picks up the new flag immediately, and
        // container-root can't delete or tamper it. Plain local file writes, so
        // these stay inline (fast); collect the slow legacy docker-exec ones to
        // run in parallel afterward.
        var legacy = new List<(string ContainerId, string Flag, int Pid, int Cid)>();

        // BYOC (self-hosted) services have no platform /flag to write or exec —
        // GZCTF pushes the rotating flag to the relay's flag port, which forwards
        // it over the agent tunnel to the team's own service. Collected so the
        // TCP pushes run in parallel after the loop.
        var byocChallengeIds = (await db.GameChallenges
            .Where(c => c.GameId == gameId && c.AdSelfHosted)
            .Select(c => c.Id)
            .ToListAsync(token)).ToHashSet();
        var byocPush = new List<(string Ip, string Flag, int Pid, int Cid)>();

        foreach (var (ts, flag) in toInject)
        {
            if (byocChallengeIds.Contains(ts.ChallengeId))
            {
                if (ts.Container?.IP is { Length: > 0 } relayIp)
                    byocPush.Add((relayIp, flag, ts.ParticipationId, ts.ChallengeId));
                continue;
            }

            if (flagMount.IsBindMounted(ts.ParticipationId, ts.ChallengeId))
            {
                try { flagMount.Write(ts.ParticipationId, ts.ChallengeId, flag); injected++; }
                catch (Exception e)
                {
                    logger.LogWarning(e, "A&D flag write failed for team={Tid} challenge={Cid}",
                        ts.ParticipationId, ts.ChallengeId);
                }
                continue;
            }

            // Legacy fallback: docker exec into the (writable) /flag — used for
            // containers launched before the bind-mount feature, or when the
            // mount isn't available. Skipped on K8s (no Docker provider).
            if (docker is null) continue;
            if (ts.Container?.ContainerId is { Length: > 0 } cid)
                legacy.Add((cid, flag, ts.ParticipationId, ts.ChallengeId));
        }

        // Legacy docker-exec writes in parallel (bounded) so round-advance stays
        // sub-second even with many pre-bind-mount containers.
        if (legacy.Count > 0 && docker is not null)
        {
            var injectedLegacy = 0;
            using var gate = new SemaphoreSlim(10, 10);
            await Task.WhenAll(legacy.Select(async item =>
            {
                await gate.WaitAsync(token);
                try
                {
                    await WriteFlagFileAsync(docker, item.ContainerId, item.Flag, token);
                    Interlocked.Increment(ref injectedLegacy);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "A&D flag inject failed for team={Tid} challenge={Cid}",
                        item.Pid, item.Cid);
                }
                finally { gate.Release(); }
            }));
            injected += injectedLegacy;
        }

        // BYOC relays: push the flag in parallel (bounded). Best-effort — an
        // offline team simply doesn't receive it (and the SLA checker already
        // reports them down), so a failed push never blocks round-advance.
        if (byocPush.Count > 0)
        {
            var pushed = 0;
            using var gate = new SemaphoreSlim(10, 10);
            await Task.WhenAll(byocPush.Select(async item =>
            {
                await gate.WaitAsync(token);
                try
                {
                    await PushFlagToRelayAsync(item.Ip, item.Flag, token);
                    Interlocked.Increment(ref pushed);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "A&D BYOC flag push failed for team={Tid} challenge={Cid}",
                        item.Pid, item.Cid);
                }
                finally { gate.Release(); }
            }));
            injected += pushed;
        }

        logger.SystemLog(
            $"A&D round advanced: game={gameId} round={nextNumber} flags_planted={toInject.Count} flags_injected={injected} koth_tokens={kothTokensMinted}",
            TaskStatus.Success, LogLevel.Information);

        // New round → flags rotated, a tick of SLA settled. Refresh the cached
        // scoreboard/timeline (background regen; CacheMaker de-bounces bursts).
        await serviceProvider.GetRequiredService<Cache.CacheHelper>()
            .FlushAdScoreboardCache(gameId, token);

        return new Result(round, toInject.Count);
    }

    /// <summary>
    /// Push the round's flag to a BYOC relay's flag port (see
    /// <see cref="AdContainerManager.ByocFlagPort"/>). The relay forwards it over
    /// the agent tunnel to the team's self-hosted service, which writes its own
    /// <c>/flag</c>. One flag per connection — raw bytes then close (EOF), which
    /// the relay reads as the new flag. Short connect+write timeout so an offline
    /// or wedged relay can't stall round-advance.
    /// </summary>
    private static async Task PushFlagToRelayAsync(string ip, string flag, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Parse(ip), AdContainerManager.ByocFlagPort, cts.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(flag), cts.Token);
    }

    /// <summary>
    /// Write the round's flag to <see cref="FlagFilePath"/> inside a running
    /// container via the Docker exec API. Flag chars are restricted to URL-
    /// safe base64 + the literal <c>flag{}</c> wrapper (see
    /// <c>AdvanceAsync</c>'s generator), so wrapping in single quotes is
    /// shell-safe — no escape needed.
    ///
    /// <para>Runs as <c>root</c> (<c>User = "0"</c>) regardless of the image's
    /// USER, and leaves the file root-owned <c>644</c>. This is what secures
    /// the flag against deletion/tampering: the platform can always re-plant
    /// it every tick (a team can't block this host-initiated exec), and a
    /// service that runs as a non-root user can <em>read</em> the flag (the
    /// intended exploit target) but cannot delete it — removing <c>/flag</c>
    /// needs write on <c>/</c>, which stays root-only — nor overwrite the
    /// root-owned file. (If the service itself runs as root, a full RCE can
    /// still wipe it for one tick; the next tick re-plants and the checker's
    /// getflag scores the gap as SLA loss.)</para>
    /// </summary>
    private static async Task WriteFlagFileAsync(
        DockerClient docker, string containerId, string flag, CancellationToken token)
    {
        var exec = await docker.Exec.CreateContainerExecAsync(containerId,
            new ContainerExecCreateParameters
            {
                AttachStdout = false,
                AttachStderr = false,
                User = "0", // root — so the plant works even for non-root service images
                Cmd = new[]
                {
                    "sh", "-c",
                    $"printf '%s' '{flag}' > {FlagFilePath} && chmod 644 {FlagFilePath}"
                }
            }, token);

        await docker.Exec.StartContainerExecAsync(exec.ID,
            new ContainerExecStartParameters(), token);
    }
}
