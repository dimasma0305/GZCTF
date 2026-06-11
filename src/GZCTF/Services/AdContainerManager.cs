using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Docker.DotNet;
using DockerModels = Docker.DotNet.Models;
using GZCTF.Models;
using GZCTF.Models.Data;
using GZCTF.Models.Internal;
using GZCTF.Services.Cache;
using GZCTF.Services.Config;
using GZCTF.Services.Container.Manager;
using GZCTF.Services.Container.Provider;
using GZCTF.Storage.Interface;
using GZCTF.Utils;
using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;

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
    AdEgressIsolationService egressIso,
    ILogger<AdContainerManager> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    // Per-hill iptables chain name for the KotH leader-cooldown (lives inside
    // the WG sidecar's netns). One chain per challenge so refreshes / lifts
    // operate independently. Apply/Lift/Destroy all call this helper rather
    // than re-formatting the string, so a rename in the future only needs to
    // happen here.
    private const string KothCooldownChainPrefix = "KOTH_CD_";
    private static string KothCooldownChain(int challengeId) =>
        $"{KothCooldownChainPrefix}{challengeId}";

    // Per-(participation, challenge) lock serializing all create/move/destroy
    // for a single service, so the reconcile loop, the accept-time ensure, and
    // self-reset/force-restart can't race into double-launches or orphans.
    private static readonly ConcurrentDictionary<(int, int), SemaphoreSlim> _serviceLocks = new();

    // KotH challenge ids whose KOTH_CD cooldown chain we've already torn down
    // (per process) — dedups the orphaned-chain cleanup so ended games aren't
    // re-processed (spawning a helper) every reconcile. A (re)launch clears the
    // mark so a re-ended game's chain is cleaned again.
    private static readonly ConcurrentDictionary<int, byte> _kothChainsTornDown = new();

    private static SemaphoreSlim LockFor(int participationId, int challengeId) =>
        _serviceLocks.GetOrAdd((participationId, challengeId), _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Run <paramref name="action"/> under the same per-challenge lock that
    /// <see cref="EnsureKothTargetsAsync"/> uses for the shared hill. Lets the
    /// checker (a separate hosted service) serialize its probe + marker-read +
    /// result persist against the reconciler's destroy/launch on a refresh tick —
    /// without that, a check that started just before a refresh would probe the
    /// freshly-launched empty container and record "no controller" for a round
    /// where the marker had been set legitimately.
    /// </summary>
    public async Task WithKothChallengeLockAsync(int challengeId, Func<Task> action, CancellationToken token)
    {
        var sem = LockFor(0, challengeId);
        await sem.WaitAsync(token);
        try { await action(); }
        finally { sem.Release(); }
    }

    /// <summary>
    /// Authoritative single-container liveness check (fresh docker inspect / pod
    /// read). Used under the per-service lock so we don't act on a stale
    /// tick-level snapshot (a concurrent launch may have replaced the container
    /// since). Returns true ("assume alive, don't relaunch") when liveness can't
    /// be determined (no provider / a query error) so only a definitive
    /// not-found / not-running / failed state triggers a relaunch.
    /// </summary>
    private static async Task<bool> IsContainerRunningAsync(
        IContainerProvider<DockerClient, DockerMetadata>? dockerProvider,
        IContainerProvider<Kubernetes, KubernetesMetadata>? k8sProvider,
        string containerId, CancellationToken token)
    {
        if (dockerProvider is not null)
        {
            try
            {
                var info = await dockerProvider.GetProvider().Containers.InspectContainerAsync(containerId, token);
                return info.State?.Running == true;
            }
            catch (DockerContainerNotFoundException) { return false; }
            catch { return true; }
        }

        if (k8sProvider is not null)
        {
            try
            {
                var pod = await k8sProvider.GetProvider().CoreV1
                    .ReadNamespacedPodAsync(containerId, k8sProvider.GetMetadata().Config.Namespace, cancellationToken: token);
                return !IsPodDead(pod);
            }
            catch (k8s.Autorest.HttpOperationException e) when (e.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return false;
            }
            catch { return true; }
        }

        return true;
    }

    /// <summary>
    /// Whether a launched A&amp;D pod has definitively failed and should be
    /// relaunched. Treats a pod as dead on: terminal phase (Failed/Succeeded —
    /// a long-running service that exited), a container stuck in an image-pull
    /// or crash back-off, or any container already terminated. A pod still
    /// pulling / starting (Pending without an error reason) is considered alive,
    /// so a freshly-launched pod isn't churned before it comes up.
    /// </summary>
    private static bool IsPodDead(V1Pod pod)
    {
        var phase = pod.Status?.Phase;
        if (phase is "Failed" or "Succeeded")
            return true;

        var statuses = pod.Status?.ContainerStatuses;
        if (statuses is null)
            return false;

        foreach (var cs in statuses)
        {
            if (cs.State?.Terminated is not null)
                return true;
            var reason = cs.State?.Waiting?.Reason;
            if (reason is "ImagePullBackOff" or "ErrImagePull" or "CrashLoopBackOff"
                or "CreateContainerError" or "RunContainerError" or "InvalidImageName")
                return true;
        }

        return false;
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
            .Where(g => g.Challenges.Any(c =>
                (c.Type == ChallengeType.AttackDefense || c.Type == ChallengeType.KingOfTheHill) && c.IsEnabled))
            .Select(g => g.Id)
            .ToListAsync(token);

        var dockerProvider = scope.ServiceProvider.GetService<IContainerProvider<DockerClient, DockerMetadata>>();
        var k8sProvider = scope.ServiceProvider.GetService<IContainerProvider<Kubernetes, KubernetesMetadata>>();
        var runningIds = await GetRunningContainerIdsAsync(dockerProvider, k8sProvider, token);

        foreach (var gameId in activeGames)
            await EnsureContainersForGameAsync(db, containerManager, dockerProvider, k8sProvider, gameId, runningIds, token);

        // Ended games: snapshot (if allowed) → destroy. Each under the
        // per-service lock so it can't race a concurrent self-reset /
        // force-restart for the same service (which could orphan a container at
        // the game-end boundary), with a fresh re-read inside the lock.
        var endedServices = await db.AdTeamServices
            .Where(ts => ts.ContainerId != null && ts.Participation.Game.EndTimeUtc < now)
            .Select(ts => new { ts.Id, ts.ParticipationId, ts.ChallengeId })
            .ToListAsync(token);

        foreach (var ended in endedServices)
        {
            var sem = LockFor(ended.ParticipationId, ended.ChallengeId);
            await sem.WaitAsync(token);
            try
            {
                var ts = await db.AdTeamServices
                    .Include(t => t.Container)
                    .Include(t => t.Participation).ThenInclude(p => p.Game)
                    .FirstOrDefaultAsync(t => t.Id == ended.Id, token);

                // Re-check under the lock: a concurrent reset may have changed
                // it, or the game may no longer be ended (extended).
                if (ts?.Container is null || ts.Participation.Game.EndTimeUtc >= now)
                    continue;

                if (ts.Participation.Game.AdAllowSnapshotDownload && ts.SnapshotBlobKey is null)
                {
                    var key = await TrySnapshotAsync(scope.ServiceProvider, ts, token);
                    if (key is not null)
                        ts.SnapshotBlobKey = key;
                }

                // Capture the filesystem diff before the container is gone, so the
                // post-game "what did they change" view works. Docker's
                // TrySnapshotAsync already set this; K8s (image-tarball snapshot is
                // Docker-only) wouldn't have, so compute it here via exec.
                if (ts.SnapshotChanges is null)
                {
                    var changes = await ComputeLiveChangesAsync(scope.ServiceProvider, ts, token);
                    if (changes is { Count: > 0 })
                        ts.SnapshotChanges = System.Text.Json.JsonSerializer.Serialize(
                            changes.Take(3000).Select(c => new { p = c.Path, k = c.Kind }));
                }

                await containerManager.DestroyContainerAsync(ts.Container, token);
                flagMount.Delete(ts.ParticipationId, ts.ChallengeId);
                ts.ContainerId = null;
                await db.SaveChangesAsync(token);
                logger.SystemLog($"A&D container destroyed (game ended): team={ts.ParticipationId} challenge={ts.ChallengeId}",
                    TaskStatus.Success, LogLevel.Information);
            }
            catch (Exception e)
            {
                logger.LogErrorMessage(e,
                    $"Failed to destroy A&D container (game ended) for service={ended.Id}");
            }
            finally
            {
                sem.Release();
            }

            // Game's over for this service — drop its lock so the static map
            // doesn't accumulate one semaphore per (team, challenge) forever
            // across many games. A relaunch (game extended) just re-adds it.
            _serviceLocks.TryRemove((ended.ParticipationId, ended.ChallengeId), out _);
        }

        // Ended games — KotH hills + cooldown chains. Same shape as the A&D
        // teardown above: destroy the container, then tear down the
        // per-challenge cooldown chain in the WG sidecar so it doesn't leak
        // across games. KotH uses participationId 0 in the lock map (shared hill).
        var endedHills = await db.KothTargets
            .Where(t => t.ContainerId != null && t.Game.EndTimeUtc < now)
            .Select(t => new { t.Id, t.ChallengeId })
            .ToListAsync(token);

        foreach (var ended in endedHills)
        {
            var sem = LockFor(0, ended.ChallengeId);
            await sem.WaitAsync(token);
            try
            {
                var target = await db.KothTargets
                    .Include(t => t.Container)
                    .Include(t => t.Game)
                    .FirstOrDefaultAsync(t => t.Id == ended.Id, token);

                // Re-check under the lock: game may have been extended.
                if (target?.Container is null || target.Game.EndTimeUtc >= now)
                    continue;

                await containerManager.DestroyContainerAsync(target.Container, token);
                target.ContainerId = null;
                await db.SaveChangesAsync(token);

                // Tear down the cooldown chain — Docker-only feature, so the
                // call is a no-op on K8s (no sidecar id → returns early).
                if (dockerProvider is not null)
                {
                    await DestroyKothCooldownChainAsync(dockerProvider, ended.ChallengeId, token);
                    _kothChainsTornDown.TryAdd(ended.ChallengeId, 0);
                }

                logger.SystemLog($"KotH hill destroyed (game ended): challenge={ended.ChallengeId}",
                    TaskStatus.Success, LogLevel.Information);
            }
            catch (Exception e)
            {
                logger.LogErrorMessage(e, $"Failed to destroy KotH hill (game ended) target={ended.Id}");
            }
            finally
            {
                sem.Release();
            }

            _serviceLocks.TryRemove((0, ended.ChallengeId), out _);
        }

        // Orphaned cooldown chains: a hill whose container was already gone
        // (ContainerId null) before game-end is skipped by the loop above, so its
        // KOTH_CD_<id> chain + DOCKER-USER jump would leak in the WG sidecar.
        // Tear those down too — once per process (the set dedups so ended games
        // aren't re-processed every pass; idempotent teardown no-ops if absent).
        if (dockerProvider is not null)
        {
            var orphanChainChallengeIds = await db.KothTargets
                .Where(t => t.ContainerId == null && t.Game.EndTimeUtc < now)
                .Select(t => t.ChallengeId)
                .Distinct()
                .ToListAsync(token);

            foreach (var cid in orphanChainChallengeIds)
            {
                if (!_kothChainsTornDown.TryAdd(cid, 0)) continue; // already handled this run
                try { await DestroyKothCooldownChainAsync(dockerProvider, cid, token); }
                catch (Exception e)
                {
                    _kothChainsTornDown.TryRemove(cid, out _); // let it retry next pass
                    logger.LogWarning(e, "KotH orphaned cooldown-chain teardown failed for challenge={Cid}", cid);
                }
            }
        }
    }

    /// <summary>
    /// Set of container IDs (Docker container IDs / K8s pod names) that look
    /// <b>alive</b> right now, used to reconcile against real liveness instead
    /// of trusting the stored <see cref="ContainerStatus"/>. Returns null if no
    /// container provider is registered or the listing fails — callers then fall
    /// back to the status-only behavior so a transient provider hiccup can't
    /// trigger a relaunch storm. The K8s set excludes pods that have
    /// definitively failed (<see cref="IsPodDead"/>) but keeps still-starting
    /// pods, so the cheap pre-check only flags genuinely-dead services for the
    /// authoritative re-check.
    /// </summary>
    private async Task<HashSet<string>?> GetRunningContainerIdsAsync(
        IContainerProvider<DockerClient, DockerMetadata>? dockerProvider,
        IContainerProvider<Kubernetes, KubernetesMetadata>? k8sProvider,
        CancellationToken token)
    {
        if (dockerProvider is not null)
        {
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

        if (k8sProvider is not null)
        {
            try
            {
                var pods = await k8sProvider.GetProvider().CoreV1.ListNamespacedPodAsync(
                    k8sProvider.GetMetadata().Config.Namespace, cancellationToken: token);
                return pods.Items.Where(p => !IsPodDead(p)).Select(p => p.Metadata.Name).ToHashSet();
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "A&D reconcile: listing pods failed; using DB status only this tick");
                return null;
            }
        }

        return null;
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
            // Stream the gzipped image to a temp FILE, then upload from disk — buffering the
            // whole (potentially multi-hundred-MB) tarball in a MemoryStream risked OOM at
            // end-of-game when snapshotting large A&D images.
            var tmpPath = Path.Combine(Path.GetTempPath(), $"ad-snapshot-{ts.Id}-{Guid.NewGuid():N}.tar.gz");
            try
            {
                await using (var tmp = new FileStream(tmpPath, FileMode.Create, FileAccess.Write,
                                 FileShare.None, 81920, useAsync: true))
                await using (var gz = new GZipStream(tmp, CompressionLevel.Fastest, leaveOpen: true))
                    await imageStream.CopyToAsync(gz, token);

                await using var upload = new FileStream(tmpPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 81920, useAsync: true);
                await blobStorage.WriteAsync(blobKey, upload, append: false, cancellationToken: token);
            }
            finally
            {
                try { File.Delete(tmpPath); } catch { /* best-effort temp cleanup */ }
            }

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

    // Runtime/churn paths kept OUT of the "what did the team change" diff: the
    // SLA checker drops a fresh probe file every tick (e.g. /tmp/notes/chk*),
    // k8s injects /etc/hosts etc., the flag sidecar rewrites its mount, package
    // caches/logs churn. None are deliberate team patches — and including them
    // buries the real change AND makes the manifest differ every tick, defeating
    // the snapshot dedup (unbounded AdServiceSnapshots growth).
    private static readonly string[] NoiseChangePrefixes =
    [
        "/tmp/", "/run/", "/var/run/", "/var/log/", "/var/cache/", "/var/tmp/",
        "/var/lib/apt/", "/proc/", "/sys/", "/dev/", "/gzctf-flag/", "/root/.cache/"
    ];

    private static readonly HashSet<string> NoiseChangeExact = new(StringComparer.Ordinal)
    {
        // /flag is the Docker flag bind-mount (K8s uses /gzctf-flag/, covered by the
        // prefix above) — `docker diff` reports its mount point as a layer change,
        // but it's the platform's flag delivery, not a team change.
        "/flag", "/gzctf-flag",
        "/tmp", "/run", "/etc/hosts", "/etc/resolv.conf", "/etc/hostname", "/etc/mtab"
    };

    internal static bool IsNoiseChangePath(string p) =>
        NoiseChangeExact.Contains(p) ||
        NoiseChangePrefixes.Any(pre => p.StartsWith(pre, StringComparison.Ordinal)) ||
        // Python bytecode cache — regenerated on first import, not a team change.
        p.Contains("__pycache__", StringComparison.Ordinal);

    /// <summary>
    /// Collapse a raw <c>docker diff</c> change list to the changed leaf files and
    /// drop runtime/churn noise. <c>docker diff</c> reports every ancestor directory
    /// of a change (e.g. <c>/usr</c>, <c>/usr/local</c>, … leading to a touched
    /// file); keep only entries that aren't a strict parent of a deeper entry, then
    /// filter out the flag mount, <c>__pycache__</c>, <c>/run</c>, … (see
    /// <see cref="IsNoiseChangePath"/>). Pure — extracted from the Docker branch of
    /// <see cref="ComputeLiveChangesAsync"/> so it can be unit-tested.
    /// </summary>
    internal static List<(string Path, int Kind)> FilterAndCollapseChanges(
        IReadOnlyList<(string Path, int Kind)> entries)
    {
        var paths = entries.Select(e => e.Path).ToList();
        return entries
            .Where(e => !paths.Any(o =>
                o.Length > e.Path.Length && o.StartsWith(e.Path + "/", StringComparison.Ordinal)))
            .Where(e => !IsNoiseChangePath(e.Path))
            .Select(e => (e.Path, e.Kind))
            .ToList();
    }

    /// <summary>Human-readable summary of what the change-diff hides (see
    /// <see cref="IsNoiseChangePath"/> + the Docker ancestor-collapse). Surfaced in
    /// AdOps so an operator knows the "Changes" view is a filtered blacklist — and
    /// that an attacker foothold dropped into one of these paths won't show here
    /// (use the shell / raw inspection for that).</summary>
    public static readonly string[] NoiseFilterCategories =
    [
        "flag mount (/flag, /gzctf-flag)",
        "/tmp and /run",
        "/var/log, /var/cache, /var/tmp, /var/run, package caches",
        "/proc, /sys, /dev",
        "Python __pycache__ and *.pyc",
        "directories that only contain a changed file (ancestor dirs)"
    ];

    /// <summary>
    /// On-demand filesystem diff of a team's <em>live</em> container — the admin
    /// "what did they change" view during a running game (the post-game snapshot
    /// captures the same thing at game end). Returns (path, kind) entries, capped,
    /// runtime/churn paths filtered out (see <see cref="IsNoiseChangePath"/>),
    /// or null if there's no live container / the provider can't compute it.
    ///
    /// <para>Docker: <c>docker diff</c> (InspectChanges) vs the baseline image —
    /// precise add/modify/delete. Kubernetes: <c>exec</c> a <c>find … -newer
    /// /proc/1</c> in the pod (files modified since the container started) — an
    /// mtime heuristic (no add/modify/delete distinction, misses deletions) since
    /// there's no layer-diff API. Both work on the running container.</para>
    /// </summary>
    public async Task<List<(string Path, int Kind)>?> ComputeLiveChangesAsync(
        IServiceProvider scopeServices, AdTeamService ts, CancellationToken token)
    {
        if (ts.Container?.ContainerId is not { Length: > 0 } cid)
            return null;

        const int maxEntries = 3000;

        var dockerProvider = scopeServices.GetService<IContainerProvider<DockerClient, DockerMetadata>>();
        if (dockerProvider is not null)
        {
            try
            {
                var changes = await dockerProvider.GetProvider().Containers.InspectChangesAsync(cid, token);
                if (changes is null) return [];
                var entries = changes.Take(maxEntries).Select(c => (Path: c.Path, Kind: (int)c.Kind)).ToList();
                // docker diff lists EVERY ancestor dir of a change (the K8s `find
                // -type f` doesn't); collapse to leaf paths and drop runtime/churn
                // noise (the flag mount, __pycache__, /run, …).
                return FilterAndCollapseChanges(entries);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "A&D live diff (docker) failed for service={Sid}", ts.Id);
                return null;
            }
        }

        var k8sProvider = scopeServices.GetService<IContainerProvider<Kubernetes, KubernetesMetadata>>();
        if (k8sProvider is not null)
        {
            try
            {
                var ns = k8sProvider.GetMetadata().Config.Namespace;
                // -xdev stays on the container rootfs; -newer /proc/1 ≈ "modified
                // since PID 1 started". Prune the churn dirs (the checker writes a
                // probe file every tick under /tmp etc.) so the scan is cheap and
                // returns deliberate changes; the IsNoiseChangePath filter below is
                // the authoritative exclusion (also covers the Docker path).
                const string find =
                    "find / -xdev \\( -path /tmp -o -path /run -o -path /var/log -o -path /var/cache " +
                    "-o -path /var/tmp -o -path /var/lib/apt -o -path /proc -o -path /sys -o -path /dev " +
                    "-o -path /gzctf-flag \\) -prune -o -newer /proc/1 -type f -print 2>/dev/null | head -n 3000";
                var stdout = await ExecCaptureStdoutAsync(k8sProvider.GetProvider(), ns, cid, cid,
                    ["sh", "-c", find], token);
                return stdout
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(p => !IsNoiseChangePath(p))
                    .Take(maxEntries)
                    .Select(p => (p, 0))
                    .ToList();
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "A&D live diff (k8s exec) failed for service={Sid}", ts.Id);
                return null;
            }
        }

        return null;
    }

    /// <summary>One-shot <c>exec</c> capturing stdout (channel 1) from a pod over
    /// the K8s exec WebSocket. tty:false so stdout/stderr stay on separate
    /// channels; we accumulate channel-1 bytes (handling message fragmentation)
    /// until the socket closes.</summary>
    private static async Task<string> ExecCaptureStdoutAsync(
        Kubernetes client, string ns, string pod, string container, string[] command, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        using var ws = await client.WebSocketNamespacedPodExecAsync(
            pod, ns, command, container, stderr: false, stdin: false, stdout: true, tty: false,
            cancellationToken: cts.Token);

        var sb = new StringBuilder();
        var buf = new byte[16 * 1024];
        var cont = -1; // channel of an in-progress (fragmented) message, else -1
        while (ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token); }
            catch { break; }
            if (r.MessageType == WebSocketMessageType.Close || r.Count == 0)
                break;

            int ch, off, len;
            if (cont < 0) { ch = buf[0]; off = 1; len = r.Count - 1; } // first byte = channel
            else { ch = cont; off = 0; len = r.Count; }
            cont = r.EndOfMessage ? -1 : ch;

            if (ch == 1 && len > 0) // stdout
                sb.Append(Encoding.UTF8.GetString(buf, off, len));
        }

        return sb.ToString();
    }

    #region A&D file inspection (read a single file from the live container + the baseline image)

    /// <summary>Max bytes read per file (256 KiB). The reader pulls one extra byte
    /// to flag truncation.</summary>
    internal const int MaxFileBytes = 256 * 1024;

    /// <summary>argv for reading a file: base64 of the first <see cref="MaxFileBytes"/>+1
    /// bytes (or the literal <c>__NOFILE__</c> when absent). The path is a positional
    /// param (<c>$1</c>), never interpolated into the script → no shell injection.</summary>
    private static string[] FileReadCmd(string path) =>
    [
        "sh", "-c",
        $"if [ -f \"$1\" ]; then head -c {MaxFileBytes + 1} \"$1\" | base64 | tr -d '\\n'; else printf __NOFILE__; fi",
        "x", path
    ];

    /// <summary>Read a file from the team's <em>running</em> container (Docker exec
    /// / K8s exec). Null when there's no live container, no provider, or the file
    /// is absent. Returns the (capped) bytes + a truncation flag.</summary>
    public async Task<(byte[] Data, bool Truncated)?> ReadCurrentFileBytesAsync(
        IServiceProvider scopeServices, AdTeamService ts, string path, CancellationToken token)
    {
        if (ts.Container?.ContainerId is not { Length: > 0 } cid)
            return null;

        try
        {
            var dockerProvider = scopeServices.GetService<IContainerProvider<DockerClient, DockerMetadata>>();
            if (dockerProvider is not null)
            {
                // Read straight from the container filesystem via the daemon's tar
                // archive API — no shell/coreutils needed in the image, exact bytes.
                try
                {
                    var resp = await dockerProvider.GetProvider().Containers.GetArchiveFromContainerAsync(
                        cid, new DockerModels.ContainerPathStatParameters { Path = path }, statOnly: false, token);
                    return DecodeFileOutput(await ReadTarSingleFileAsync(resp.Stream, token));
                }
                catch (DockerApiException) { return null; } // path absent / not found
            }

            var k8sProvider = scopeServices.GetService<IContainerProvider<Kubernetes, KubernetesMetadata>>();
            if (k8sProvider is not null)
                return DecodeFileOutput(await ExecCaptureStdoutAsync(
                    k8sProvider.GetProvider(), k8sProvider.GetMetadata().Config.Namespace, cid, cid, FileReadCmd(path), token));
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "A&D read current file failed: service={Sid} path={Path}", ts.Id, path);
        }
        return null;
    }

    /// <summary>Read the same file from the challenge <em>image</em> (the baseline)
    /// via a throwaway one-shot container/pod. Cached per (image, path) — the image
    /// is immutable. Null when the image lacks the file (e.g. a team-added file).</summary>
    public async Task<(byte[] Data, bool Truncated)?> ReadBaselineFileBytesAsync(
        IServiceProvider scopeServices, string image, string path, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(image))
            return null;

        var cache = scopeServices.GetService<CacheHelper>();
        var key = "_AdBaseFile_" +
                  Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{image}\n{path}")));

        var b64 = cache is null ? null : await cache.GetStringAsync(key, token);
        if (b64 is null)
        {
            try
            {
                var dockerProvider = scopeServices.GetService<IContainerProvider<DockerClient, DockerMetadata>>();
                var k8sProvider = scopeServices.GetService<IContainerProvider<Kubernetes, KubernetesMetadata>>();
                b64 = dockerProvider is not null
                    ? await ReadFileFromDockerImageAsync(dockerProvider.GetProvider(), image, path, token)
                    : k8sProvider is not null
                        ? await ReadFileFromK8sImageAsync(k8sProvider, image, path, token)
                        : null;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "A&D read baseline file failed: image={Img} path={Path}", image, path);
            }

            b64 ??= "__NOFILE__";
            if (cache is not null)
                await cache.SetStringAsync(key, b64,
                    new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromDays(7) }, token);
        }

        return DecodeFileOutput(b64);
    }

    internal static (byte[] Data, bool Truncated)? DecodeFileOutput(string? output)
    {
        var s = output?.Trim();
        if (string.IsNullOrEmpty(s) || s == "__NOFILE__")
            return null;
        byte[] raw;
        try { raw = Convert.FromBase64String(s); }
        catch { return null; }
        var truncated = raw.Length > MaxFileBytes;
        if (truncated) raw = raw[..MaxFileBytes];
        return (raw, truncated);
    }

    /// <summary>Extract the first regular file from a Docker tar archive stream as
    /// base64 (capped at <see cref="MaxFileBytes"/>+1 bytes so <see cref="DecodeFileOutput"/>
    /// flags truncation). Null when the archive has no regular-file entry.</summary>
    /// <remarks>
    /// Docker's <c>GetArchiveFromContainerAsync</c> returns the tar over a chunked
    /// HTTP response. <c>System.Formats.Tar.TarReader</c> reads it with small,
    /// exactly-sized reads (512-byte headers, per-entry substreams), which trips a
    /// bug in Docker.DotNet's <c>ChunkedReadStream</c> — it throws
    /// <see cref="EndOfStreamException"/> ("read past the end of the stream") at the
    /// final chunk instead of returning 0, aborting the read mid-entry. So first
    /// drain the response into a seekable <see cref="MemoryStream"/> with large
    /// CopyTo-style reads (the pattern the snapshot export already uses reliably),
    /// then parse the complete buffer. The buffer is capped so a huge file can't
    /// exhaust memory; the +16 KiB margin covers the tar header, 512-byte padding,
    /// the end-of-archive trailer, and any extended-header blocks.
    /// </remarks>
    internal static async Task<string?> ReadTarSingleFileAsync(Stream tar, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        await using (tar)
        {
            var bufferCap = MaxFileBytes + 16 * 1024;
            var chunk = new byte[81920];
            try
            {
                int n;
                while (buffer.Length < bufferCap &&
                       (n = await tar.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, bufferCap - buffer.Length)), token)) > 0)
                    buffer.Write(chunk, 0, n);
            }
            catch (EndOfStreamException)
            {
                // Docker.DotNet chunked-stream quirk: the real archive bytes are
                // already buffered by the time it throws on the read past the end.
            }
        }
        buffer.Position = 0;

        using var reader = new TarReader(buffer);
        while (await reader.GetNextEntryAsync(cancellationToken: token) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                || entry.DataStream is null)
                continue;

            var cap = MaxFileBytes + 1;
            using var ms = new MemoryStream();
            var buf = new byte[16 * 1024];
            int read;
            while (ms.Length < cap &&
                   (read = await entry.DataStream.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, cap - ms.Length)), token)) > 0)
                ms.Write(buf, 0, read);
            return Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length);
        }
        return null;
    }

    private async Task<string?> ReadFileFromDockerImageAsync(
        DockerClient docker, string image, string path, CancellationToken token)
    {
        // Create (never start) a container from the image and read the file straight
        // from its filesystem via the archive API — no entrypoint run, no in-image tools.
        var pars = new DockerModels.CreateContainerParameters { Image = image };
        string? id = null;
        try
        {
            DockerModels.CreateContainerResponse created;
            try { created = await docker.Containers.CreateContainerAsync(pars, token); }
            catch (DockerImageNotFoundException)
            {
                await docker.Images.CreateImageAsync(new DockerModels.ImagesCreateParameters { FromImage = image }, null,
                    new Progress<DockerModels.JSONMessage>(_ => { }), token);
                created = await docker.Containers.CreateContainerAsync(pars, token);
            }

            id = created.ID;
            try
            {
                var resp = await docker.Containers.GetArchiveFromContainerAsync(
                    id, new DockerModels.ContainerPathStatParameters { Path = path }, statOnly: false, token);
                return await ReadTarSingleFileAsync(resp.Stream, token);
            }
            catch (DockerApiException) { return null; } // path not in the image
        }
        finally
        {
            if (id is not null)
                try { await docker.Containers.RemoveContainerAsync(id, new DockerModels.ContainerRemoveParameters { Force = true }, CancellationToken.None); }
                catch { /* best-effort */ }
        }
    }

    private async Task<string?> ReadFileFromK8sImageAsync(
        IContainerProvider<Kubernetes, KubernetesMetadata> provider, string image, string path, CancellationToken token)
    {
        var client = provider.GetProvider();
        var ns = provider.GetMetadata().Config.Namespace;
        var name = $"ad-fileread-{Guid.NewGuid().ToString("N")[..12]}".ToValidRFC1123String("ad-fileread");

        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = ns,
                Labels = new Dictionary<string, string>
                {
                    ["gzctf.gzti.me/ResourceId"] = name,
                    ["gzctf.role"] = "ad-fileread"
                }
            },
            Spec = new V1PodSpec
            {
                Containers =
                [
                    new V1Container
                    {
                        Name = "reader",
                        Image = image,
                        ImagePullPolicy = provider.GetMetadata().Config.ImagePullPolicy,
                        Command = FileReadCmd(path),
                        Resources = new V1ResourceRequirements
                        {
                            Limits = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("500m"), ["memory"] = new("128Mi") },
                            Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("10m"), ["memory"] = new("32Mi") }
                        }
                    }
                ],
                RestartPolicy = "Never",
                AutomountServiceAccountToken = false
            }
        };

        try { await client.CreateNamespacedPodAsync(pod, ns, cancellationToken: token); }
        catch (Exception e)
        {
            logger.LogWarning(e, "A&D fileread pod create failed image={Img}", image);
            return null;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(60)); // allow for image pull
            var done = false;
            while (!cts.IsCancellationRequested)
            {
                var p = await client.ReadNamespacedPodAsync(name, ns, cancellationToken: cts.Token);
                if (p.Status?.Phase is "Succeeded" or "Failed") { done = true; break; }
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            }
            if (!done) return null;

            await using var stream = await client.CoreV1.ReadNamespacedPodLogAsync(name, ns, cancellationToken: token);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(token);
        }
        finally
        {
            try { await client.CoreV1.DeleteNamespacedPodAsync(name, ns, cancellationToken: CancellationToken.None); }
            catch { /* best-effort */ }
        }
    }

    #endregion

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
        var k8sProvider = scope.ServiceProvider.GetService<IContainerProvider<Kubernetes, KubernetesMetadata>>();
        var runningIds = await GetRunningContainerIdsAsync(dockerProvider, k8sProvider, token);
        await EnsureContainersForGameAsync(db, containerManager, dockerProvider, k8sProvider, gameId, runningIds, token);
    }

    /// <summary>
    /// Tear down every A&amp;D service container + KotH hill container (and the
    /// per-challenge KotH cooldown chains) for a game — called when a game is being
    /// DELETED so its containers don't outlive the DB rows. Without this, deleting a
    /// game (e.g. via a cascade repo-binding delete) drops the rows but leaves the
    /// Docker/K8s containers running forever, since the reconciler keys teardown off
    /// the game's EndTimeUtc — which no longer exists once the game is gone.
    ///
    /// <para>Best-effort and idempotent: each destroy is wrapped so one failure
    /// doesn't abort the rest, and it runs BEFORE the DB rows are removed so it can
    /// still resolve the container records. Nulls ContainerId as it goes so a
    /// concurrent reconciler pass won't double-destroy.</para>
    /// </summary>
    public Task DestroyContainersForGameAsync(int gameId, CancellationToken token = default)
        => DestroyContainersInternalAsync(gameId, null, null, token);

    /// <summary>
    /// Tear down the A&amp;D / KotH containers (and KotH cooldown chains) for a SINGLE
    /// challenge — called when an A&amp;D or KotH challenge is being DELETED mid-game.
    /// Same rationale as <see cref="DestroyContainersForGameAsync"/>: the row delete
    /// cascades the AdTeamService / KothTarget records away, so the reconciler can never
    /// find the orphaned containers again and they'd run until the game's EndTimeUtc cron.
    /// Must run BEFORE the challenge rows are removed.
    /// </summary>
    public Task DestroyContainersForChallengeAsync(int challengeId, CancellationToken token = default)
        => DestroyContainersInternalAsync(null, challengeId, null, token);

    /// <summary>
    /// Tear down the A&amp;D service containers (+ host flag-mount files) for a SINGLE
    /// participation (team) — called when a TEAM is being deleted mid-game. Same rationale as
    /// the game/challenge variants: deleting the team cascade-removes its AdTeamService rows, so
    /// the reconciler can never find the containers again and they'd run until game end (and the
    /// host flag file would leak forever). Must run BEFORE the participation rows are removed.
    /// KotH hills are per-challenge/shared (not per-team), so they are intentionally NOT touched.
    /// </summary>
    public Task DestroyContainersForParticipationAsync(int participationId, CancellationToken token = default)
        => DestroyContainersInternalAsync(null, null, participationId, token);

    private async Task DestroyContainersInternalAsync(int? gameId, int? challengeId, int? participationId,
        CancellationToken token = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var containerManager = scope.ServiceProvider.GetRequiredService<IContainerManager>();
        var dockerProvider = scope.ServiceProvider.GetService<IContainerProvider<DockerClient, DockerMetadata>>();

        // A&D service containers (TeamId=<participationId>), scoped to a game, a single challenge,
        // or a single participation (team).
        var servicesQuery = db.AdTeamServices.Where(ts => ts.ContainerId != null);
        if (gameId is { } sgid)
            servicesQuery = servicesQuery.Where(ts => ts.Participation.GameId == sgid);
        if (challengeId is { } scid)
            servicesQuery = servicesQuery.Where(ts => ts.ChallengeId == scid);
        if (participationId is { } spid)
            servicesQuery = servicesQuery.Where(ts => ts.ParticipationId == spid);
        var services = await servicesQuery
            .Include(ts => ts.Container)
            .ToListAsync(token);

        foreach (var ts in services)
        {
            var sem = LockFor(ts.ParticipationId, ts.ChallengeId);
            await sem.WaitAsync(token);
            try
            {
                if (ts.Container is null)
                    continue;
                await containerManager.DestroyContainerAsync(ts.Container, token);
                flagMount.Delete(ts.ParticipationId, ts.ChallengeId);
                ts.ContainerId = null;
                await db.SaveChangesAsync(token);
            }
            catch (Exception e)
            {
                logger.LogErrorMessage(e,
                    $"Failed to destroy A&D container (game deleted): service={ts.Id}");
            }
            finally
            {
                sem.Release();
                _serviceLocks.TryRemove((ts.ParticipationId, ts.ChallengeId), out _);
            }
        }

        // KotH hill containers (TeamId=koth-<challengeId>) + cooldown chains, same scoping.
        // A KotH hill is per-challenge/shared, NOT per-team — so a pure per-participation teardown
        // (team delete) must leave every hill running for the remaining teams.
        List<KothTarget> hills;
        if (participationId is { } && gameId is null && challengeId is null)
        {
            hills = [];
        }
        else
        {
            var hillsQuery = db.KothTargets.AsQueryable();
            if (gameId is { } hgid)
                hillsQuery = hillsQuery.Where(t => t.GameId == hgid);
            if (challengeId is { } hcid)
                hillsQuery = hillsQuery.Where(t => t.ChallengeId == hcid);
            hills = await hillsQuery.Include(t => t.Container).ToListAsync(token);
        }

        foreach (var target in hills)
        {
            var sem = LockFor(0, target.ChallengeId);
            await sem.WaitAsync(token);
            try
            {
                if (target.Container is not null)
                {
                    await containerManager.DestroyContainerAsync(target.Container, token);
                    target.ContainerId = null;
                    await db.SaveChangesAsync(token);
                }
                // Docker-only feature; no-op on K8s (no sidecar id → returns early).
                if (dockerProvider is not null)
                {
                    await DestroyKothCooldownChainAsync(dockerProvider, target.ChallengeId, token);
                    _kothChainsTornDown.TryAdd(target.ChallengeId, 0);
                }
            }
            catch (Exception e)
            {
                logger.LogErrorMessage(e,
                    $"Failed to destroy KotH hill (game deleted): challenge={target.ChallengeId}");
            }
            finally
            {
                sem.Release();
                _serviceLocks.TryRemove((0, target.ChallengeId), out _);
            }
        }

        var scopeDesc = challengeId is { } cidLog ? $"challenge {cidLog}" : $"game {gameId}";
        logger.SystemLog(
            $"A&D/KotH containers torn down for deleted {scopeDesc}: services={services.Count} hills={hills.Count}",
            TaskStatus.Success, LogLevel.Information);
    }

    private async Task EnsureContainersForGameAsync(
        AppDbContext db,
        IContainerManager containerManager,
        IContainerProvider<DockerClient, DockerMetadata>? dockerProvider,
        IContainerProvider<Kubernetes, KubernetesMetadata>? k8sProvider,
        int gameId,
        IReadOnlySet<string>? runningDockerIds,
        CancellationToken token)
    {
        // Pull A&D + KotH challenges for this game.
        var adChallenges = await db.GameChallenges
            .Where(c => c.GameId == gameId && c.Type == ChallengeType.AttackDefense && c.IsEnabled)
            .ToListAsync(token);
        var kothChallenges = await db.GameChallenges
            .Where(c => c.GameId == gameId && c.Type == ChallengeType.KingOfTheHill && c.IsEnabled)
            .ToListAsync(token);

        if (adChallenges.Count == 0 && kothChallenges.Count == 0)
            return;

        // --- Attack & Defense: one container per (accepted team, challenge) ---
        if (adChallenges.Count > 0)
        {
            var participations = await db.Participations
                .Where(p => p.GameId == gameId && p.Status == ParticipationStatus.Accepted)
                .Select(p => p.Id)
                .ToListAsync(token);

            if (participations.Count > 0)
            {
                // Existing AdTeamService rows for this game.
                var existing = await db.AdTeamServices
                    .Where(ts => participations.Contains(ts.ParticipationId))
                    .Include(ts => ts.Container)
                    .ToListAsync(token);

                var keyed = existing.ToDictionary(ts => (ts.ParticipationId, ts.ChallengeId));

                // For every (participation, challenge): cheap, lock-free pre-check on
                // the bulk-loaded state + tick-level running set. Only when something
                // looks off do we take the per-service lock and re-verify — so the
                // common (healthy) case stays lock- and inspect-free.
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

                    await EnsureOneServiceAsync(db, containerManager, dockerProvider, k8sProvider, participationId, challenge, token);
                }
            }
        }

        // --- King of the Hill: ONE shared container per challenge (+ 5-tick refresh) ---
        if (kothChallenges.Count > 0)
        {
            // Known limitation (D1): the leader-cooldown mechanic is implemented
            // via iptables in the WG sidecar's netns, which only the Docker
            // provider can exec into. On Kubernetes the hill itself still launches
            // and the marker-based scoring still works, but there's no per-tick
            // throttle on whoever just held the hill — they can immediately
            // re-pwn the freshly-reset container. KotH on K8s is therefore
            // strictly easier for whoever's ahead than on Docker; mention it
            // once per process per game so operators see it in the log.
            if (dockerProvider is null && k8sProvider is not null)
                WarnKothK8sCooldownOnce(gameId);

            await EnsureKothTargetsAsync(db, containerManager, dockerProvider, k8sProvider, gameId, kothChallenges, token);
        }
    }

    /// <summary>Per-process set of games we've already warned about K8s+KotH
    /// for; bounded by the number of distinct K8s+KotH games seen in the lifetime
    /// of the process (small).</summary>
    private static readonly HashSet<int> _kothK8sWarnedGames = [];
    private void WarnKothK8sCooldownOnce(int gameId)
    {
        lock (_kothK8sWarnedGames)
        {
            if (!_kothK8sWarnedGames.Add(gameId)) return;
        }
        logger.SystemLog(
            $"KotH on Kubernetes (game={gameId}): leader-cooldown is NOT applied — the front-runner can " +
            "re-pwn the freshly-reset hill without a tick of network block. Implement K8s NetworkPolicy " +
            "parity to remove this limitation; for now KotH on K8s is best-effort.",
            TaskStatus.Pending, LogLevel.Warning);
    }

    /// <summary>
    /// Reconcile the shared King-of-the-Hill target containers for a game — one per
    /// KotH challenge (not per team). Launches a missing/dead hill, and every
    /// <c>Game.KothRefreshTicks</c> ticks resets it to base image (wiping footholds
    /// and the <c>/koth/king</c> marker) and hands the current per-challenge score
    /// leader a one-tick network cooldown.
    /// </summary>
    private async Task EnsureKothTargetsAsync(
        AppDbContext db,
        IContainerManager containerManager,
        IContainerProvider<DockerClient, DockerMetadata>? dockerProvider,
        IContainerProvider<Kubernetes, KubernetesMetadata>? k8sProvider,
        int gameId,
        List<GameChallenge> kothChallenges,
        CancellationToken token)
    {
        var latestRound = await db.AdRounds
            .Where(r => r.GameId == gameId)
            .OrderByDescending(r => r.Number)
            .Select(r => (int?)r.Number)
            .FirstOrDefaultAsync(token) ?? 0;

        var refreshTicks = await db.Games
            .Where(g => g.Id == gameId)
            .Select(g => g.KothRefreshTicks)
            .FirstOrDefaultAsync(token) ?? 5;
        if (refreshTicks < 1) refreshTicks = 1;

        foreach (var challenge in kothChallenges)
        {
            // participation 0 keys the shared hill lock — no real team owns it.
            var sem = LockFor(0, challenge.Id);
            await sem.WaitAsync(token);
            try
            {
                var target = await db.KothTargets
                    .Include(t => t.Container)
                    .FirstOrDefaultAsync(t => t.GameId == gameId && t.ChallengeId == challenge.Id, token);

                var cid = target?.Container?.ContainerId;
                var dead = cid is { Length: > 0 }
                           && !await IsContainerRunningAsync(dockerProvider, k8sProvider, cid, token);
                // Refresh BETWEEN rounds, not during them. After every refreshTicks
                // rounds of play we refresh on the transition INTO the next round —
                // so the team that held the hill at the boundary round still has
                // their KothControlResult persisted (by AdCheckerService) before the
                // container gets wiped. Fires at rounds 6, 11, 16, ... for the default
                // refreshTicks=5; rounds 1-5 use the original hill, 6-10 the first
                // refreshed hill, and so on. Combined with the per-challenge lock
                // taken by AdCheckerService.WithKothChallengeLockAsync, this closes
                // the refresh-vs-checker race where the just-launched hill was being
                // probed (empty marker) before the boundary round was scored.
                // Edge-triggered on CROSSING a refresh-window boundary, not on the
                // exact boundary round. The reconcile (15s) and the round scheduler
                // are unsynchronized, so at fast ticks a pass can observe round N then
                // N+2 and never land on the exact `(round-1)%refreshTicks==0` value —
                // with the old exact test that window's refresh (and its leader
                // cooldown) was lost for the rest of the game, letting the first holder
                // keep the hill forever. Comparing window indices fires once per window
                // and self-heals a skipped boundary on the next pass. Window index of
                // round R = (R-1)/refreshTicks; LastRefreshRound 0 (never refreshed) maps
                // to window 0 via C# truncation, so rounds 1..refreshTicks never refresh.
                var lastRefresh = target?.LastRefreshRound ?? 0;
                var dueRefresh = latestRound > refreshTicks
                                 && (latestRound - 1) / refreshTicks > (lastRefresh - 1) / refreshTicks;

                // Operator flipped AdAllowEgress on this challenge mid-game — the
                // running hill was launched on the wrong bridge. Force a refresh
                // immediately so the new policy takes effect; otherwise it'd be
                // ignored until the next 5-tick boundary (M4). Mirrors A&D's
                // LaunchedWithEgress drift handling at line 1317.
                var egressDrift = target?.LaunchedWithEgress is { } le && le != challenge.AdAllowEgress;

                var needsLaunch = target is null || target.ContainerId is null || target.Container is null
                                  || target.Container.Status == ContainerStatus.Destroyed || dead
                                  || dueRefresh || egressDrift;

                if (needsLaunch)
                {
                    // Destroy any existing container first — for a refresh this is the
                    // "reset to base" that wipes footholds + the marker.
                    if (target?.Container is not null)
                    {
                        target.Container.Status = ContainerStatus.Destroyed;
                        try { await containerManager.DestroyContainerAsync(target.Container, token); }
                        catch { /* already gone / raced */ }
                        target.ContainerId = null;
                        await db.SaveChangesAsync(token);
                    }

                    await LaunchKothTargetAsync(db, containerManager, gameId, challenge, target, dockerProvider is null, token);

                    if (dueRefresh)
                    {
                        // Refetch the freshly-launched target so we have the new
                        // container's IP for the cooldown rule.
                        target = await db.KothTargets
                            .Include(t => t.Container)
                            .FirstOrDefaultAsync(t => t.GameId == gameId && t.ChallengeId == challenge.Id, token);

                        // Only advance the refresh bookkeeping + install the cooldown if the
                        // relaunch ACTUALLY produced a live hill container. A silently-failed
                        // launch (e.g. a transient Docker create failure) leaves Container
                        // null; advancing LastRefreshRound anyway would flip next tick's
                        // dueRefresh to false and PERMANENTLY skip this window's cooldown
                        // while leaving the hill down. Leaving it unchanged makes the next
                        // tick retry the refresh. Cooldown is applied before the save so a
                        // crash in between re-fires dueRefresh (the apply is idempotent).
                        if (target?.Container is { } c && c.Status != ContainerStatus.Destroyed)
                        {
                            if (dockerProvider is not null)
                                await ApplyKothLeaderCooldownAsync(db, dockerProvider, gameId, challenge.Id, latestRound, token);

                            target.LastRefreshRound = latestRound;
                            await db.SaveChangesAsync(token);
                        }
                    }
                }

                // Cooldown lifecycle (Docker only) — lift the previous refresh's
                // cooldown once the round advances past it. Best-effort.
                if (dockerProvider is not null && !dueRefresh
                    && latestRound > (target?.LastRefreshRound ?? 0))
                    await LiftKothCooldownAsync(dockerProvider, challenge.Id, token);
            }
            finally
            {
                sem.Release();
            }
        }
    }

    /// <summary>
    /// At a KotH refresh, hand the per-challenge <b>recent leader</b> a one-tick
    /// network cooldown: drop their VPN /32(s) → the hill IP on the WireGuard
    /// sidecar's FORWARD chain (which runs before its MASQUERADE, so the source
    /// is still the client IP). Rules live in a per-hill chain
    /// (<c>KOTH_CD_&lt;id&gt;</c>) that <see cref="LiftKothCooldownAsync"/>
    /// flushes next tick.
    ///
    /// <para>"Recent leader" = team with the highest <c>HoldCredit − Penalty</c>
    /// across the rounds <em>since the last refresh</em> (the window that just
    /// ended). Using cumulative score instead would permanently throttle whoever
    /// pulls ahead early in the game — a team that wins rounds 1-5 would get
    /// cooldown'd at every refresh forever even if a different team is winning
    /// recent rounds. The window-based definition rotates the punishment among
    /// whoever is actually leading right now and matches the comment about
    /// preventing the front-runner from immediately re-pwning the fresh hill.</para>
    ///
    /// Ties → all tied leaders; nobody scored in the window → nobody blocked.
    /// Docker MVP; best-effort (swallows errors so it never breaks the reconcile).
    /// </summary>
    private async Task ApplyKothLeaderCooldownAsync(
        AppDbContext db,
        IContainerProvider<DockerClient, DockerMetadata> dockerProvider,
        int gameId, int challengeId, int round, CancellationToken token)
    {
        try
        {
            // Re-evaluate the cooldown from scratch each refresh: lift any prior
            // foothold block for THIS hill up front, before the early-returns below can
            // short-circuit. Otherwise a refresh window that earns no new cooldown (no
            // leader / no hill / no sidecar) never overwrites a stale _kothCooldowns
            // entry, and at KothRefreshTicks=1 the LiftKothCooldownAsync gate
            // (!dueRefresh) is structurally unsatisfiable — so the stale foothold→hill
            // DROP would wrongly block a legit ad→koth play for the rest of the game
            // (esp. once Docker re-hands the freed hill IP). SetKothFootholdCooldown at
            // the end re-arms it only if this window actually has a leader.
            egressIso.ClearKothFootholdCooldown(challengeId);
            egressIso.RequestReapply();

            // Window = rounds since the last refresh boundary, lower bound INCLUSIVE so the
            // boundary round (the re-pwn round right after the previous refresh) is counted
            // exactly once — in this window. With an exclusive `> lastRefresh` it was dropped
            // every refresh, and the window was empty (cooldown never fired) for very short
            // refresh intervals. On the first refresh (LastRefreshRound=0) it's rounds
            // 1..round-1, unchanged (round numbers start at 1).
            var lastRefresh = await db.KothTargets
                .Where(t => t.GameId == gameId && t.ChallengeId == challengeId)
                .Select(t => (int?)t.LastRefreshRound)
                .FirstOrDefaultAsync(token) ?? 0;

            var scores = await db.KothControlResults
                .Where(r => r.GameId == gameId && r.ChallengeId == challengeId
                         && r.ControllingParticipationId != null
                         && r.AdRound.Number >= lastRefresh && r.AdRound.Number < round)
                .GroupBy(r => r.ControllingParticipationId!.Value)
                .Select(g => new { Pid = g.Key, Score = g.Sum(x => x.HoldCredit - x.Penalty) })
                .ToListAsync(token);

            var positive = scores.Where(s => s.Score > 0).ToList();
            if (positive.Count == 0)
                return; // nobody held the hill in the window — no cooldown earned

            var best = positive.Max(s => s.Score);
            var leaders = positive.Where(s => Math.Abs(s.Score - best) < 1e-9).Select(s => s.Pid).ToHashSet();

            var hillIp = await db.KothTargets
                .Where(t => t.GameId == gameId && t.ChallengeId == challengeId && t.Container != null)
                .Select(t => t.Container!.IP)
                .FirstOrDefaultAsync(token);
            if (string.IsNullOrEmpty(hillIp))
                return;

            var sidecar = await ReadVpnSidecarIdAsync(token);
            if (sidecar is null)
                return;

            var docker = dockerProvider.GetProvider();
            var chain = KothCooldownChain(challengeId);
            await EnsureKothChainAsync(docker, sidecar, chain, token); // create + flush + jump

            var peerIps = await db.AdVpnPeers
                .Where(p => p.GameId == gameId && p.RevokedAt == null && leaders.Contains(p.ParticipationId))
                .Select(p => p.AssignedIp)
                .ToListAsync(token);

            var blocked = 0;
            foreach (var raw in peerIps)
            {
                var ip = (raw ?? string.Empty).Split('/')[0];
                if (!System.Net.IPAddress.TryParse(ip, out _))
                    continue;
                await ExecOnSidecarAsync(docker, sidecar,
                    ["iptables", "-A", chain, "-s", ip, "-d", hillIp, "-j", "DROP"], token);
                blocked++;
            }

            // The sidecar chain above only covers the leaders' VPN path to the hill.
            // In a mixed A&D+KotH game a leader can re-plant from its OWN A&D foothold
            // (host-bridge traffic the WG sidecar never sees, and egress deliberately
            // allows ad→koth). Register the leaders' A&D container IPs with the egress
            // service so its host DOCKER-USER chain drops foothold→hill for the cooldown
            // window too (lifted in LiftKothCooldownAsync). `leaders` are already
            // game-scoped (from this game's KothControlResults).
            var footholdIps = await db.AdTeamServices
                .Where(s => leaders.Contains(s.ParticipationId) && s.Container != null && s.Container.IP != "")
                .Select(s => s.Container!.IP)
                .ToListAsync(token);
            egressIso.SetKothFootholdCooldown(challengeId, footholdIps, hillIp);
            if (footholdIps.Count > 0)
                egressIso.RequestReapply();

            logger.SystemLog(
                $"KotH cooldown: blocked {blocked} leader VPN IP(s) + {footholdIps.Count} foothold IP(s) "
                + $"from hill challenge={challengeId} for round {round}",
                TaskStatus.Success, LogLevel.Information);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "KotH cooldown apply failed for challenge={Cid}", challengeId);
        }
    }

    /// <summary>Lift a KotH cooldown by flushing the per-hill chain (idempotent).</summary>
    private async Task LiftKothCooldownAsync(
        IContainerProvider<DockerClient, DockerMetadata> dockerProvider, int challengeId, CancellationToken token)
    {
        // Drop the host-side foothold block first (in-memory + a reapply), so it's
        // cleared even if the sidecar exec below fails.
        egressIso.ClearKothFootholdCooldown(challengeId);
        egressIso.RequestReapply();
        try
        {
            var sidecar = await ReadVpnSidecarIdAsync(token);
            if (sidecar is null)
                return;
            await ExecOnSidecarAsync(dockerProvider.GetProvider(), sidecar,
                ["iptables", "-F", KothCooldownChain(challengeId)], token);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "KotH cooldown lift failed for challenge={Cid}", challengeId);
        }
    }

    /// <summary>
    /// Fully tear down a hill's cooldown chain on game-end: unhook from FORWARD,
    /// flush, and remove the chain itself. Without this, the per-game
    /// <c>KOTH_CD_&lt;cid&gt;</c> chains stay attached to FORWARD inside the WG
    /// sidecar forever; reusing the same challengeId for a new game would have
    /// the new game inherit the previous game's leader-block rules. Idempotent
    /// (every step silently no-ops if the chain doesn't exist).
    /// </summary>
    private async Task DestroyKothCooldownChainAsync(
        IContainerProvider<DockerClient, DockerMetadata> dockerProvider, int challengeId, CancellationToken token)
    {
        // Also drop any lingering host-side foothold block for this hill on game-end.
        egressIso.ClearKothFootholdCooldown(challengeId);
        try
        {
            var sidecar = await ReadVpnSidecarIdAsync(token);
            if (sidecar is null)
                return;
            var chain = KothCooldownChain(challengeId);
            await ExecOnSidecarAsync(dockerProvider.GetProvider(), sidecar,
                ["sh", "-c",
                 $"iptables -D FORWARD -j {chain} 2>/dev/null; iptables -F {chain} 2>/dev/null; iptables -X {chain} 2>/dev/null; true"],
                token);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "KotH cooldown destroy failed for challenge={Cid}", challengeId);
        }
    }

    /// <summary>Ensure the per-hill cooldown chain exists, is jumped to from FORWARD,
    /// and starts empty (flushed) before fresh blocks are added.
    ///
    /// <para><b>Known caveat (M1):</b> uses <c>iptables -I FORWARD -j chain</c> which
    /// inserts at position 1. On the stock <c>linuxserver/wireguard</c> sidecar this
    /// is fine — its own PostUp inserts go before, so our chain ends up running
    /// before MASQUERADE (we need the original source IP). A custom sidecar image
    /// whose entrypoint also runs <c>iptables -I FORWARD</c> on every restart could
    /// land in front of our chain depending on whose insert wins the race; the
    /// observable symptom would be cooldowns silently not taking effect (no error;
    /// the leader just keeps reaching the hill). If you ever swap the WG sidecar
    /// image, verify with <c>iptables -L FORWARD --line-numbers</c> that
    /// KOTH_CD_* sits above any MASQUERADE rule.</para></summary>
    private static async Task EnsureKothChainAsync(
        DockerClient docker, string sidecar, string chain, CancellationToken token) =>
        await ExecOnSidecarAsync(docker, sidecar,
            ["sh", "-c",
             $"iptables -N {chain} 2>/dev/null; iptables -C FORWARD -j {chain} 2>/dev/null || iptables -I FORWARD -j {chain}; iptables -F {chain}"],
            token);

    /// <summary>Read the WireGuard sidecar container id from the shared
    /// <c>sidecar.id</c> file (written by the sidecar entrypoint, hex-validated).
    /// Null if VPN isn't configured / the file is missing or malformed.</summary>
    private async Task<string?> ReadVpnSidecarIdAsync(CancellationToken token)
    {
        using var scope = scopeFactory.CreateScope();
        var configDir = scope.ServiceProvider.GetRequiredService<IConfiguration>()["Ad:Vpn:ConfigDir"];
        if (string.IsNullOrWhiteSpace(configDir))
            return null;
        var path = Path.Combine(configDir, "sidecar.id");
        if (!File.Exists(path))
            return null;
        var raw = (await File.ReadAllTextAsync(path, token)).Trim();
        return raw.Length is >= 12 and <= 64 && raw.All(Uri.IsHexDigit) ? raw : null;
    }

    /// <summary>Fire-and-forget docker exec on the WG sidecar (it has iptables +
    /// NET_ADMIN). No stdout capture needed — the cooldown rules are write-only.</summary>
    private static async Task ExecOnSidecarAsync(
        DockerClient docker, string sidecar, string[] cmd, CancellationToken token)
    {
        var exec = await docker.Exec.CreateContainerExecAsync(sidecar,
            new DockerModels.ContainerExecCreateParameters { AttachStdout = false, AttachStderr = false, Cmd = cmd },
            token);
        await docker.Exec.StartContainerExecAsync(exec.ID, new DockerModels.ContainerExecStartParameters(), token);
    }

    /// <summary>
    /// Launch the single shared container for a KotH challenge and record it on the
    /// <see cref="KothTarget"/> (one per game·challenge). Unlike A&amp;D there is no
    /// platform-planted flag — teams write their own rotating token into the marker —
    /// so no flag bind-mount / pull-sidecar is configured.
    /// </summary>
    private async Task LaunchKothTargetAsync(
        AppDbContext db,
        IContainerManager containerManager,
        int gameId,
        GameChallenge challenge,
        KothTarget? existing,
        bool isK8s,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(challenge.ContainerImage))
        {
            logger.SystemLog($"KotH challenge {challenge.Id} has no ContainerImage; skipping launch",
                TaskStatus.Failed, LogLevel.Warning);
            return;
        }

        var config = new ContainerConfig
        {
            Image = challenge.ContainerImage,
            TeamId = $"koth-{challenge.Id}",
            ChallengeId = challenge.Id,
            GameId = gameId,
            ExposedPort = challenge.ExposePort ?? 80,
            // No platform flag for KotH — teams plant their own token into /koth/king.
            CPUCount = challenge.CPUCount ?? 1,
            MemoryLimit = challenge.MemoryLimit ?? 128,
            StorageLimit = challenge.StorageLimit ?? 256,
            // The hill must be reachable by every team; honour AdAllowEgress for the
            // open/isolated bridge like A&D (default open).
            NetworkMode = challenge.AdAllowEgress ? NetworkMode.Open : NetworkMode.Isolated,
            EnableTrafficCapture = false
        };

        Models.Data.Container? container;
        try { container = await containerManager.CreateContainerAsync(config, token); }
        catch (Exception e)
        {
            logger.LogErrorMessage(e, $"Failed to launch KotH hill for challenge={challenge.Id}");
            return;
        }
        if (container is null)
        {
            logger.SystemLog($"KotH hill launch returned null for challenge={challenge.Id}",
                TaskStatus.Failed, LogLevel.Warning);
            return;
        }

        var game = await db.Games.FirstOrDefaultAsync(g => g.Id == gameId, token);
        if (game is not null)
            container.ExpectStopAt = game.EndTimeUtc;

        try
        {
            await db.Containers.AddAsync(container, token);
            if (existing is null)
                await db.KothTargets.AddAsync(new KothTarget
                {
                    GameId = gameId,
                    ChallengeId = challenge.Id,
                    ContainerId = container.Id,
                    LaunchedWithEgress = challenge.AdAllowEgress
                }, token);
            else
            {
                existing.ContainerId = container.Id;
                // Record what we launched with so EnsureKothTargetsAsync can
                // detect a future AdAllowEgress toggle and force a refresh.
                existing.LaunchedWithEgress = challenge.AdAllowEgress;
            }

            await db.SaveChangesAsync(token);
        }
        catch (Exception e)
        {
            logger.LogErrorMessage(e, $"KotH launch: recording container failed; destroying orphan challenge={challenge.Id}");
            try { await containerManager.DestroyContainerAsync(container, token); }
            catch (Exception de) { logger.LogErrorMessage(de, "KotH launch: orphan cleanup also failed"); }
            return;
        }

        logger.SystemLog($"KotH hill launched: challenge={challenge.Id} ip={container.IP}:{container.Port}",
            TaskStatus.Success, LogLevel.Information);

        // New hill IP is live — contain it now, not up to a full pass later.
        egressIso.RequestReapply();
        // Hill is alive again — clear any stale "chain torn down" mark so its
        // cooldown chain is re-cleaned if the game later ends.
        _kothChainsTornDown.TryRemove(challenge.Id, out _);
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
        IContainerProvider<Kubernetes, KubernetesMetadata>? k8sProvider,
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

            // Authoritative liveness via a fresh inspect / pod read (not the
            // stale tick snapshot) — avoids relaunching a container another
            // holder just created since the snapshot was taken.
            var deadInDocker = ts?.Container?.ContainerId is { Length: > 0 } cid
                               && !await IsContainerRunningAsync(dockerProvider, k8sProvider, cid, token);

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

            await LaunchOneAsync(db, containerManager, participationId, challenge, ts, dockerProvider is null, token);
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
            egressIso.RequestReapply();
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
        bool isK8s,
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
        // Flag delivery differs by provider:
        //   Docker → read-only host-backed /flag bind mount (undeletable);
        //            warmup the host file first (docker bind-mounts a missing
        //            source as an empty *directory*, which would break /flag).
        //   K8s    → PULL model: the flag-writer sidecar polls FlagPullUrl and
        //            writes /gzctf-flag/flag (no exec; RO-enforced on real nodes).
        var flagFilePath = "/flag";
        string? flagBindSource = null;
        string? flagPullUrl = null;

        if (isK8s)
        {
            flagFilePath = "/gzctf-flag/flag";
            using var cfgScope = scopeFactory.CreateScope();
            var baseUrl = cfgScope.ServiceProvider.GetRequiredService<IConfiguration>()["Ad:FlagPullBaseUrl"]
                ?.TrimEnd('/');
            var xorKey = cfgScope.ServiceProvider.GetService<IConfigService>()?.GetXorKey();
            if (!string.IsNullOrEmpty(baseUrl) && xorKey is not null)
            {
                var podToken = AdTokenUtils.PodFlagToken(participationId, challenge.Id, xorKey);
                flagPullUrl =
                    $"{baseUrl}/api/Game/{participation.GameId}/Ad/PodFlag/{participationId}/{challenge.Id}/{podToken}";
            }
            else
                logger.SystemLog(
                    "A&D on K8s: Ad:FlagPullBaseUrl not configured — flags can't be delivered to team pods",
                    TaskStatus.Failed, LogLevel.Warning);
        }
        else
        {
            if (flagMount.Available)
            {
                flagMount.EnsureWarmup(participationId, challenge.Id);
                flagBindSource = flagMount.BindSource(participationId, challenge.Id);
            }
        }

        var config = new ContainerConfig
        {
            Image = challenge.ContainerImage,
            TeamId = participation.TeamId.ToString(),
            ChallengeId = challenge.Id,
            GameId = participation.GameId,
            UserId = participation.FirstUserId,
            ExposedPort = challenge.ExposePort ?? 80,
            // Flag intentionally unset for A&D — see note above; the flag file is the source of truth.
            FlagFilePath = flagFilePath,
            FlagBindSource = flagBindSource,
            FlagPullUrl = flagPullUrl,
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

        // New container IP is live — contain it now, not up to a full pass later.
        egressIso.RequestReapply();
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
        var isK8s = scope.ServiceProvider.GetService<IContainerProvider<DockerClient, DockerMetadata>>() is null;

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

            await LaunchOneAsync(db, containerManager, ts.ParticipationId, ts.Challenge, ts, isK8s, token);

            // LaunchOneAsync is void with several silent-failure paths (image gone,
            // create error) that leave ts.ContainerId null. Only report success + burn
            // the self-reset cooldown when a live container actually resulted —
            // otherwise we'd tell the player "reset done", leave the box DOWN, and lock
            // them out for AdResetCooldownMinutes. Reconcile retries within ≤15s.
            // Mirrors the KotH refresh guard (`target?.Container is { } ...`).
            if (ts.ContainerId is null)
                return false;

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
