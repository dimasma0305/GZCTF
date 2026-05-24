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
///   <item>For ended games: destroy any still-running A&amp;D containers.</item>
/// </list>
///
/// <para>MVP scope: uses the existing per-game Docker/K8s network (no dedicated
/// ad-net + ebtables L2 isolation yet — Phase 1 follow-up). Late-join works
/// implicitly: an Accepted-mid-game Participation gets containers on the next
/// reconcile tick (≤ <see cref="PollInterval"/> seconds).</para>
/// </summary>
public sealed class AdContainerManager(
    IServiceScopeFactory scopeFactory,
    ILogger<AdContainerManager> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

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

        foreach (var gameId in activeGames)
            await EnsureContainersForGameAsync(db, containerManager, gameId, token);

        // Ended games: snapshot (if allowed) → destroy.
        var endedGameTeamServices = await db.AdTeamServices
            .Where(ts => ts.ContainerId != null)
            .Where(ts => ts.Participation.Game.EndTimeUtc < now)
            .Include(ts => ts.Container)
            .Include(ts => ts.Challenge)
            .Include(ts => ts.Participation)
            .ToListAsync(token);

        foreach (var ts in endedGameTeamServices.Where(ts => ts.Container is not null))
        {
            try
            {
                if (ts.Challenge.AdAllowSnapshotDownload && ts.SnapshotBlobKey is null)
                {
                    var key = await TrySnapshotAsync(scope.ServiceProvider, ts, token);
                    if (key is not null)
                        ts.SnapshotBlobKey = key;
                }
                await containerManager.DestroyContainerAsync(ts.Container!, token);
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
        await EnsureContainersForGameAsync(db, containerManager, gameId, token);
    }

    private async Task EnsureContainersForGameAsync(
        AppDbContext db,
        IContainerManager containerManager,
        int gameId,
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

        // For every (participation, challenge), ensure a live container.
        foreach (var participationId in participations)
        foreach (var challenge in adChallenges)
        {
            keyed.TryGetValue((participationId, challenge.Id), out var ts);

            var needsLaunch = ts is null
                              || ts.ContainerId is null
                              || ts.Container is null
                              || ts.Container.Status == ContainerStatus.Destroyed;

            if (!needsLaunch)
                continue;

            await LaunchOneAsync(db, containerManager, participationId, challenge, ts, token);
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

        // Egress: when AdAllowEgress is false (default), restrict via NetworkMode.
        // Phase 3 will add the proper firewall layer; for MVP we lean on the
        // existing Open/Isolated knob.
        var networkMode = challenge.AdAllowEgress ? NetworkMode.Open : NetworkMode.Isolated;

        var config = new ContainerConfig
        {
            Image = challenge.ContainerImage,
            TeamId = participation.TeamId.ToString(),
            ChallengeId = challenge.Id,
            GameId = participation.GameId,
            UserId = participation.FirstUserId,
            ExposedPort = challenge.ExposePort ?? 80,
            Flag = null,
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

        await db.Containers.AddAsync(container, token);

        if (existing is null)
        {
            await db.AdTeamServices.AddAsync(new AdTeamService
            {
                ParticipationId = participationId,
                ChallengeId = challenge.Id,
                ContainerId = container.Id
            }, token);
        }
        else
        {
            existing.ContainerId = container.Id;
        }

        await db.SaveChangesAsync(token);

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
}
