using System.Collections.Concurrent;
using System.IO.Compression;
using Docker.DotNet;
using DockerModels = Docker.DotNet.Models;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Models.Internal;
using GZCTF.Services.Container.Manager;
using GZCTF.Services.Container.Provider;
using GZCTF.Storage.Interface;
using GZCTF.Utils;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Services;

/// <summary>
/// Manages the lifecycle of per-team-per-service Attack &amp; Defense containers.
///
/// <para>Reconciles desired state every <see cref="PollInterval"/>:</para>
/// <list type="bullet">
///   <item>For each active A&amp;D game (running window + has AttackDefense challenges):
///         ensure every accepted Participation has a live container for every A&amp;D
///         challenge. Launch missing ones via <see cref="IContainerManager"/>.</item>
///   <item>For ended games: snapshot (Docker only) + destroy any still-running
///         A&amp;D containers.</item>
/// </list>
///
/// <para>MVP scope: uses the existing per-provider network (no dedicated
/// ad-net + ebtables L2 isolation yet — Phase 1 follow-up). Late-join works
/// implicitly: an Accepted-mid-game Participation gets containers on the next
/// reconcile tick (≤ <see cref="PollInterval"/> seconds).</para>
///
/// <para>Provider compatibility — works on both Docker and Kubernetes via the
/// <see cref="IContainerManager"/> abstraction. K8s-only operators should
/// apply <c>scripts/ad-k8s-networkpolicy.yaml</c> for L4 isolation between
/// A&amp;D pods and the control plane. See <c>scripts/ad-k8s-readme.md</c>
/// for the parity gaps (L2 isolation, snapshot, per-game namespace) that
/// stay Docker-only for v1.</para>
/// </summary>
public sealed class AdContainerManager(
    IServiceScopeFactory scopeFactory,
    AdFlagMountService flagMount,
    ILogger<AdContainerManager> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    // Per-(participation, challenge) lock serializing all create/move/destroy
    // for a single service, so the reconcile loop, the accept-time ensure, and
    // self-reset/force-restart can't race into double-launches or orphans.
    private static readonly ConcurrentDictionary<(int, int), SemaphoreSlim> _serviceLocks = new();

    private static SemaphoreSlim LockFor(int participationId, int challengeId) =>
        _serviceLocks.GetOrAdd((participationId, challengeId), _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Authoritative single-container liveness check (fresh docker inspect).
    /// Used under the per-service lock so we don't act on a stale tick-level
    /// snapshot (a concurrent launch may have replaced the container since).
    /// Returns true ("assume alive, don't relaunch") when liveness can't be
    /// determined — no Docker provider (K8s) or an inspect error — so only a
    /// definitive not-found / not-running triggers a relaunch.
    /// </summary>
    private static async Task<bool> IsContainerRunningAsync(
        IContainerProvider<DockerClient, DockerMetadata>? provider, string containerId, CancellationToken token)
    {
        if (provider is null) return true;
        try
        {
            var info = await provider.GetProvider().Containers.InspectContainerAsync(containerId, token);
            return info.State?.Running == true;
        }
        catch (DockerContainerNotFoundException) { return false; }
        catch { return true; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.SystemLog("AdContainerManager started; reconciling every 15s",
            TaskStatus.Pending, LogLevel.Information);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileOnceAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogErrorMessage(e,
                    "AdContainerManager reconcile loop failed; will retry");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReconcileOnceAsync(CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var containerManager = scope.ServiceProvider.GetRequiredService<IContainerManager>();
        var now = DateTimeOffset.UtcNow;

        // Active games: ensure containers exist for every accepted team × A&D challenge.
        var activeGames = await db.Games
            .Where(g => g.StartTimeUtc <= now && now <= g.EndTimeUtc)
            .Include(g => g.Challenges)
            .Where(g => g.Challenges.Any(c => c.Type == ChallengeType.AttackDefense && c.IsEnabled))
            .Select(g => g.Id)
            .ToListAsync(token);

        var dockerProvider = scope.ServiceProvider.GetService<IContainerProvider<DockerClient, DockerMetadata>>();
        var runningIds = await GetRunningContainerIdsAsync(dockerProvider, token);

        foreach (var gameId in activeGames)
            await EnsureContainersForGameAsync(db, containerManager, dockerProvider, gameId, runningIds, token);

        // Ended games: snapshot (if allowed) → destroy.
        var endedGameTeamServices = await db.AdTeamServices
            .Where(ts => ts.ContainerId != null)
            .Where(ts => ts.Participation.Game.EndTimeUtc < now)
            .Include(ts => ts.Container)
            .Include(ts => ts.Challenge)
            .Include(ts => ts.Participation).ThenInclude(p => p.Game)
            .ToListAsync(token);

        foreach (var ts in endedGameTeamServices.Where(ts => ts.Container is not null))
        {
            try
            {
                // Snapshot-download is game-wide policy.
                if (ts.Participation.Game.AdAllowSnapshotDownload && ts.SnapshotBlobKey is null)
                {
                    var key = await TrySnapshotAsync(scope.ServiceProvider, ts, token);
                    if (key is not null)
                        ts.SnapshotBlobKey = key;
                }
                await containerManager.DestroyContainerAsync(ts.Container!, token);
                flagMount.Delete(ts.ParticipationId, ts.ChallengeId);
                logger.SystemLog($"A&D container destroyed (game ended): team={ts.ParticipationId} challenge={ts.ChallengeId}",
                    TaskStatus.Success, LogLevel.Information);
                ts.ContainerId = null;
            }
            catch (Exception e)
            {
                logger.LogErrorMessage(e,
                    $"Failed to destroy A&D container for team={ts.ParticipationId} challenge={ts.ChallengeId}");
            }
        }

        if (endedGameTeamServices.Count > 0)
            await db.SaveChangesAsync(token);
    }

    /// <summary>
    /// Set of docker container IDs that are actually <b>running</b> right now,
    /// used to reconcile against real liveness instead of trusting the stored
    /// <see cref="ContainerStatus"/>. Returns null on a K8s deploy (no Docker
    /// provider) or if the listing fails — callers then fall back to the
    /// status-only behavior so a transient docker hiccup can't trigger a
    /// relaunch storm.
    /// </summary>
    private async Task<HashSet<string>?> GetRunningContainerIdsAsync(
        IContainerProvider<DockerClient, DockerMetadata>? dockerProvider, CancellationToken token)
    {
        if (dockerProvider is null)
            return null;

        try
        {
            var list = await dockerProvider.GetProvider().Containers.ListContainersAsync(
                new DockerModels.ContainersListParameters { All = false }, token);
            return list.Select(c => c.ID).ToHashSet();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "A&D reconcile: listing running containers failed; using DB status only this tick");
            return null;
        }
    }

    /// <summary>
    /// Snapshot a team's A&amp;D container to a gzipped Docker image tarball + upload
    /// to <see cref="IBlobStorage"/>. Returns the blob key on success, null on
    /// failure (including silently-skipped K8s deployments — snapshot is Docker-
    /// only for v1; K8s parity is a future enhancement).
    /// </summary>
    public async Task<string?> TrySnapshotAsync(
        IServiceProvider scopeServices, AdTeamService ts, CancellationToken token)
    {
        if (ts.Container is null) return null;

        var dockerProvider = scopeServices.GetService<IContainerProvider<DockerClient, DockerMetadata>>();
        if (dockerProvider is null)
        {
            logger.SystemLog(
                "A&D snapshot skipped: Docker provider not registered (K8s deployment). Snapshot is Docker-only for v1.",
                TaskStatus.Pending, LogLevel.Debug);
            return null;
        }

        var docker = dockerProvider.GetProvider();
        var blobStorage = scopeServices.GetRequiredService<IBlobStorage>();
        var imageRef = $"ad-snapshot-{ts.Id}:{ts.Participation.GameId}";

        try
        {
            // Capture `docker diff` (writable-layer changes vs the baseline
            // image) BEFORE committing/destroying — this is the "what did the
            // team change" data. Best-effort: a failure here shouldn't abort
            // the snapshot. Capped so a pathological container can't bloat the
            // row.
            try
            {
                var changes = await docker.Containers.InspectChangesAsync(ts.Container.ContainerId, token);
                if (changes is { Count: > 0 })
                {
                    var trimmed = changes
                        .Take(3000)
                        .Select(c => new { p = c.Path, k = (int)c.Kind })
                        .ToList();
                    ts.SnapshotChanges = System.Text.Json.JsonSerializer.Serialize(trimmed);
                }
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "A&D snapshot: docker diff failed for team={Tid} challenge={Cid}",
                    ts.ParticipationId, ts.ChallengeId);
            }

            await docker.Images.CommitContainerChangesAsync(new DockerModels.CommitContainerChangesParameters
            {
                ContainerID = ts.Container.ContainerId,
                RepositoryName = $"ad-snapshot-{ts.Id}",
                Tag = ts.Participation.GameId.ToString(),
                Comment = $"A&D end-of-game snapshot: team={ts.ParticipationId} challenge={ts.ChallengeId}"
            }, token);

            await using var imageStream =
                await docker.Images.SaveImageAsync(imageRef, token);

            var blobKey = $"ad-snapshots/{ts.Participation.GameId}/{ts.ParticipationId}-{ts.ChallengeId}.tar.gz";
            using var ms = new MemoryStream();
            await using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                await imageStream.CopyToAsync(gz, token);
            ms.Position = 0;
            await blobStorage.WriteAsync(blobKey, ms, append: false, cancellationToken: token);

            // Clean up the local image — the tarball is the deliverable.
            try
            {
                await docker.Images.DeleteImageAsync(imageRef,
                    new DockerModels.ImageDeleteParameters { Force = true }, token);
            }
            catch { /* best-effort cleanup */ }

            logger.SystemLog(
                $"A&D snapshot saved: team={ts.ParticipationId} challenge={ts.ChallengeId} blob={blobKey}",
                TaskStatus.Success, LogLevel.Information);
            return blobKey;
        }
        catch (Exception e)
        {
            logger.LogErrorMessage(e,
                $"A&D snapshot failed: team={ts.ParticipationId} challenge={ts.ChallengeId}");
            return null;
        }
    }

    /// <summary>
    /// Public entrypoint for one-shot ensure: called from controllers when a
    /// Participation transitions to Accepted mid-game (no need to wait for
    /// the poll loop).
    /// </summary>
    public async Task EnsureContainersForGameAsync(int gameId, CancellationToken token = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var containerManager = scope.ServiceProvider.GetRequiredService<IContainerManager>();
        var dockerProvider = scope.ServiceProvider.GetService<IContainerProvider<DockerClient, DockerMetadata>>();
        var runningIds = await GetRunningContainerIdsAsync(dockerProvider, token);
        await EnsureContainersForGameAsync(db, containerManager, dockerProvider, gameId, runningIds, token);
    }

    private async Task EnsureContainersForGameAsync(
        AppDbContext db,
        IContainerManager containerManager,
        IContainerProvider<DockerClient, DockerMetadata>? dockerProvider,
        int gameId,
        IReadOnlySet<string>? runningDockerIds,
        CancellationToken token)
    {
        // Pull A&D challenges for this game.
        var adChallenges = await db.GameChallenges
            .Where(c => c.GameId == gameId && c.Type == ChallengeType.AttackDefense && c.IsEnabled)
            .ToListAsync(token);

        if (adChallenges.Count == 0)
            return;

        // Pull all accepted participations for this game.
        var participations = await db.Participations
            .Where(p => p.GameId == gameId && p.Status == ParticipationStatus.Accepted)
            .Select(p => p.Id)
            .ToListAsync(token);

        if (participations.Count == 0)
            return;

        // Existing AdTeamService rows for this game.
        var existing = await db.AdTeamServices
            .Where(ts => participations.Contains(ts.ParticipationId))
            .Include(ts => ts.Container)
            .ToListAsync(token);

        var keyed = existing.ToDictionary(ts => (ts.ParticipationId, ts.ChallengeId));

        // For every (participation, challenge): cheap, lock-free pre-check on the
        // bulk-loaded state + tick-level running set. Only when something looks
        // off do we take the per-service lock and re-verify authoritatively —
        // so the common (healthy) case stays lock- and inspect-free.
        foreach (var participationId in participations)
        foreach (var challenge in adChallenges)
        {
            keyed.TryGetValue((participationId, challenge.Id), out var ts);

            var cid = ts?.Container?.ContainerId;
            var maybeDead = runningDockerIds is not null && cid is { Length: > 0 }
                            && !runningDockerIds.Contains(cid);
            var maybeDrift = ts?.LaunchedWithEgress is { } le && le != challenge.AdAllowEgress;

            var needsAction = ts is null
                              || ts.ContainerId is null
                              || ts.Container is null
                              || ts.Container.Status == ContainerStatus.Destroyed
                              || maybeDead
                              || maybeDrift;

            if (!needsAction)
                continue;

            await EnsureOneServiceAsync(db, containerManager, dockerProvider, participationId, challenge, token);
        }
    }

    /// <summary>
    /// Bring one (participation, challenge) service to the desired state under
    /// its per-service lock. Re-reads fresh state inside the lock (a concurrent
    /// reset / accept-ensure may have just acted) and uses an authoritative
    /// docker inspect for liveness. Repairs:
    /// <list type="bullet">
    ///   <item><b>Missing / dead container</b> → relaunch (cleaning the dead record).</item>
    ///   <item><b>Network drift</b> (egress toggled vs launch) on a live container
    ///         → non-destructive live network move; recreate only if the move fails.</item>
    /// </list>
    /// </summary>
    private async Task EnsureOneServiceAsync(
        AppDbContext db,
        IContainerManager containerManager,
        IContainerProvider<DockerClient, DockerMetadata>? dockerProvider,
        int participationId,
        GameChallenge challenge,
        CancellationToken token)
    {
        var sem = LockFor(participationId, challenge.Id);
        await sem.WaitAsync(token);
        try
        {
            var ts = await db.AdTeamServices
                .Include(t => t.Container)
                .FirstOrDefaultAsync(t => t.ParticipationId == participationId && t.ChallengeId == challenge.Id, token);

            // Authoritative liveness via a fresh inspect (not the stale tick
            // snapshot) — avoids relaunching a container another holder just
            // created since the snapshot was taken.
            var deadInDocker = ts?.Container?.ContainerId is { Length: > 0 } cid
                               && !await IsContainerRunningAsync(dockerProvider, cid, token);

            var networkDrift = ts?.LaunchedWithEgress is { } le && le != challenge.AdAllowEgress;

            // Network drift on a LIVE container → move it between networks in
            // place so the team keeps its patches. Recreate only if the move
            // fails.
            if (networkDrift && !deadInDocker && ts?.Container is not null && dockerProvider is not null)
            {
                if (await TryMoveContainerNetworkAsync(db, dockerProvider, ts, challenge, token))
                    return;
                deadInDocker = true; // move failed → fall through to recreate
            }

            var needsLaunch = ts is null
                              || ts.ContainerId is null
                              || ts.Container is null
                              || ts.Container.Status == ContainerStatus.Destroyed
                              || deadInDocker;

            if (!needsLaunch)
                return;

            // Clean up the dead record before relaunching: mark it Destroyed so
            // its stale IP stops being treated as live, and best-effort remove
            // any remnant so it can't linger / re-grab the IP.
            if (deadInDocker && ts!.Container is not null)
            {
                ts.Container.Status = ContainerStatus.Destroyed;
                try { await containerManager.DestroyContainerAsync(ts.Container, token); }
                catch { /* already gone, or remnant cleanup raced — fine */ }
                logger.SystemLog(
                    $"A&D container not running — relaunching: team={participationId} challenge={challenge.Id}",
                    TaskStatus.Failed, LogLevel.Warning);
            }

            await LaunchOneAsync(db, containerManager, participationId, challenge, ts, token);
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// Move a live A&amp;D container between the Open/Isolated networks in place
    /// (connect the new network, then disconnect the old) without destroying it,
    /// then re-read its new IP and record the new egress. Returns false if the
    /// move couldn't be performed (caller then recreates). Docker-only.
    /// </summary>
    private async Task<bool> TryMoveContainerNetworkAsync(
        AppDbContext db,
        IContainerProvider<DockerClient, DockerMetadata> dockerProvider,
        AdTeamService ts,
        GameChallenge challenge,
        CancellationToken token)
    {
        if (ts.Container?.ContainerId is not { Length: > 0 } cid)
            return false;

        var meta = dockerProvider.GetMetadata();
        var oldMode = (ts.LaunchedWithEgress ?? challenge.AdAllowEgress) ? NetworkMode.Open : NetworkMode.Isolated;
        var newMode = challenge.AdAllowEgress ? NetworkMode.Open : NetworkMode.Isolated;
        if (!meta.NetworkNames.TryGetValue(oldMode, out var oldName) ||
            !meta.NetworkNames.TryGetValue(newMode, out var newName))
            return false;

        var docker = dockerProvider.GetProvider();
        try
        {
            // Connect-before-disconnect so the container is never on zero networks.
            await docker.Networks.ConnectNetworkAsync(newName,
                new DockerModels.NetworkConnectParameters { Container = cid }, token);
            await docker.Networks.DisconnectNetworkAsync(oldName,
                new DockerModels.NetworkDisconnectParameters { Container = cid, Force = true }, token);

            var info = await docker.Containers.InspectContainerAsync(cid, token);
            var newIp = info.NetworkSettings?.Networks is { } nets && nets.TryGetValue(newName, out var ep)
                ? ep.IPAddress
                : null;
            if (!string.IsNullOrEmpty(newIp))
                ts.Container!.IP = newIp;

            ts.LaunchedWithEgress = challenge.AdAllowEgress;
            await db.SaveChangesAsync(token);

            logger.SystemLog(
                $"A&D container moved {oldName} → {newName} (egress changed): team={ts.ParticipationId} challenge={challenge.Id} ip={newIp}",
                TaskStatus.Success, LogLevel.Information);
            return true;
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "A&D network move failed for team={Tid} challenge={Cid}; will recreate",
                ts.ParticipationId, challenge.Id);
            return false;
        }
    }

    private async Task LaunchOneAsync(
        AppDbContext db,
        IContainerManager containerManager,
        int participationId,
        GameChallenge challenge,
        AdTeamService? existing,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(challenge.ContainerImage))
        {
            logger.SystemLog(
                $"A&D challenge {challenge.Id} has no ContainerImage; skipping launch",
                TaskStatus.Failed, LogLevel.Warning);
            return;
        }

        var participation = await db.Participations
            .Where(p => p.Id == participationId)
            .Select(p => new
            {
                p.Id,
                p.TeamId,
                GameId = p.Game.Id,
                FirstUserId = p.Members.Select(m => m.UserId).FirstOrDefault()
            })
            .FirstOrDefaultAsync(token);

        if (participation is null)
            return;

        // Egress: AdAllowEgress defaults true (open). When set false, restrict
        // via NetworkMode. Phase 3 will add the proper firewall layer; for MVP
        // we lean on the existing Open/Isolated knob.
        var networkMode = challenge.AdAllowEgress ? NetworkMode.Open : NetworkMode.Isolated;

        // A&D flags rotate every tick and live at FlagFilePath (/flag), written
        // by AdRoundService via docker exec. We deliberately do NOT set the
        // GZCTF_FLAG env var: an env baked at container creation is frozen for
        // the container's life, so it would go stale after the first rotation
        // and mislead challenge code. Only GZCTF_FLAG_FILE is surfaced (below);
        // read the live flag from that path.
        // When the read-only flag-mount is available, ensure the host-backed
        // file exists (warmup) BEFORE creating the container — docker bind-mounts
        // a missing source as an empty *directory*, which would break /flag.
        if (flagMount.Available)
            flagMount.EnsureWarmup(participationId, challenge.Id);

        var config = new ContainerConfig
        {
            Image = challenge.ContainerImage,
            TeamId = participation.TeamId.ToString(),
            ChallengeId = challenge.Id,
            GameId = participation.GameId,
            UserId = participation.FirstUserId,
            ExposedPort = challenge.ExposePort ?? 80,
            // Flag intentionally unset for A&D — see note above; /flag is the source of truth.
            FlagFilePath = "/flag",
            // Read-only host-backed /flag (undeletable by container-root) when
            // available; null falls back to the docker-exec plant.
            FlagBindSource = flagMount.Available ? flagMount.BindSource(participationId, challenge.Id) : null,
            CPUCount = challenge.CPUCount ?? 1,
            MemoryLimit = challenge.MemoryLimit ?? 128,
            StorageLimit = challenge.StorageLimit ?? 256,
            NetworkMode = networkMode,
            EnableTrafficCapture = false
        };

        Models.Data.Container? container;
        try
        {
            container = await containerManager.CreateContainerAsync(config, token);
        }
        catch (Exception e)
        {
            logger.LogErrorMessage(e,
                $"Failed to launch A&D container for team={participationId} challenge={challenge.Id}");
            return;
        }

        if (container is null)
        {
            logger.SystemLog(
                $"A&D container launch returned null for team={participationId} challenge={challenge.Id}",
                TaskStatus.Failed, LogLevel.Warning);
            return;
        }

        // A&D containers live until game end — set ExpectStopAt to the game's
        // end time so the existing ContainerChecker cron leaves them alone.
        var game = await db.Games.FirstOrDefaultAsync(g => g.Id == participation.GameId, token);
        if (game is not null)
            container.ExpectStopAt = game.EndTimeUtc;

        try
        {
            await db.Containers.AddAsync(container, token);

            if (existing is null)
            {
                await db.AdTeamServices.AddAsync(new AdTeamService
                {
                    ParticipationId = participationId,
                    ChallengeId = challenge.Id,
                    ContainerId = container.Id,
                    LaunchedWithEgress = challenge.AdAllowEgress
                }, token);
            }
            else
            {
                existing.ContainerId = container.Id;
                existing.LaunchedWithEgress = challenge.AdAllowEgress;
            }

            await db.SaveChangesAsync(token);
        }
        catch (Exception e)
        {
            // The docker container is already created; if recording it fails
            // (e.g. a concurrent launch won the unique (participation,
            // challenge) row, or a DB error) we must destroy it — otherwise it
            // leaks as an untracked orphan that nothing will ever clean up.
            logger.LogErrorMessage(e,
                $"A&D launch: recording container failed; destroying orphan team={participationId} challenge={challenge.Id}");
            try { await containerManager.DestroyContainerAsync(container, token); }
            catch (Exception de) { logger.LogErrorMessage(de, "A&D launch: orphan cleanup also failed"); }
            return;
        }

        logger.SystemLog(
            $"A&D container launched: team={participationId} challenge={challenge.Id} ip={container.IP}:{container.Port}",
            TaskStatus.Success, LogLevel.Information);
    }

    /// <summary>
    /// Restart a single team's A&amp;D container (drives the self-reset endpoint
    /// + the operator's force-restart). Destroys + recreates from the same
    /// image; new container gets a new IP.
    /// </summary>
    public async Task<bool> RestartContainerAsync(int adTeamServiceId, CancellationToken token = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var containerManager = scope.ServiceProvider.GetRequiredService<IContainerManager>();

        // Need the (participation, challenge) key to take the per-service lock
        // before mutating — otherwise a concurrent reset/reconcile could race.
        var key = await db.AdTeamServices
            .Where(t => t.Id == adTeamServiceId)
            .Select(t => new { t.ParticipationId, t.ChallengeId })
            .FirstOrDefaultAsync(token);
        if (key is null)
            return false;

        var sem = LockFor(key.ParticipationId, key.ChallengeId);
        await sem.WaitAsync(token);
        try
        {
            var ts = await db.AdTeamServices
                .Include(t => t.Container)
                .Include(t => t.Challenge)
                .FirstOrDefaultAsync(t => t.Id == adTeamServiceId, token);

            if (ts is null)
                return false;

            if (ts.Container is not null)
            {
                try { await containerManager.DestroyContainerAsync(ts.Container, token); }
                catch (Exception e) { logger.LogErrorMessage(e, $"Restart: destroy failed for {ts.Container.LogId}"); }
                ts.ContainerId = null;
                await db.SaveChangesAsync(token);
            }

            await LaunchOneAsync(db, containerManager, ts.ParticipationId, ts.Challenge, ts, token);
            ts.LastResetAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(token);
            return true;
        }
        finally
        {
            sem.Release();
        }
    }
}
