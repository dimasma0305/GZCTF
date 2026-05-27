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
            .Where(g => g.Challenges.Any(c => c.Type == ChallengeType.AttackDefense && c.IsEnabled))
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

    private async Task EnsureContainersForGameAsync(
        AppDbContext db,
        IContainerManager containerManager,
        IContainerProvider<DockerClient, DockerMetadata>? dockerProvider,
        IContainerProvider<Kubernetes, KubernetesMetadata>? k8sProvider,
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

            await EnsureOneServiceAsync(db, containerManager, dockerProvider, k8sProvider, participationId, challenge, token);
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
