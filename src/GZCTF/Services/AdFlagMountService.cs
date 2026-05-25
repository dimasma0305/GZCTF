using Docker.DotNet;
using GZCTF.Services.Container.Provider;

namespace GZCTF.Services;

/// <summary>
/// Backs each A&amp;D team-service flag with a host file that is bind-mounted
/// <b>read-only</b> into the team's container at <c>/flag</c>. This is what
/// makes the live flag undeletable/untamperable by container-root: a read-only
/// bind mount returns <c>EROFS</c> on write/unlink even to root (and
/// <c>CAP_DAC_OVERRIDE</c> doesn't bypass it — it's a mount-layer block), while
/// remounting/unmounting needs <c>CAP_SYS_ADMIN</c>, which Docker drops by
/// default. gzctf rewrites the file <b>in place</b> every tick; the container
/// sees the new flag through the shared inode.
///
/// <para>The directory is mounted into this gzctf container at
/// <see cref="GzctfDir"/>. To bind that same file into a <i>child</i> container
/// we need its <i>host</i> path, which we auto-derive by inspecting our own
/// container's mounts (with an <c>Ad:Flag:HostDir</c> config override). When the
/// directory or host path can't be resolved (e.g. the volume isn't mounted, or
/// a K8s deploy with no Docker provider), <see cref="Available"/> is false and
/// callers fall back to the legacy <c>docker exec</c> plant.</para>
/// </summary>
public sealed class AdFlagMountService
{
    /// <summary>In-container path of the shared flag directory (mounted via compose).</summary>
    public const string GzctfDir = "/app/ad-flags";

    private const string WarmupFlag = "flag{warmup-no-round-yet}";

    private readonly ILogger<AdFlagMountService> _logger;
    private readonly Lazy<string?> _hostDir;

    public AdFlagMountService(
        IConfiguration config,
        IServiceProvider serviceProvider,
        ILogger<AdFlagMountService> logger)
    {
        _logger = logger;
        _hostDir = new Lazy<string?>(() => ResolveHostDir(config, serviceProvider));
    }

    /// <summary>True when the read-only-bind-mount plant is usable (dir mounted + host path known).</summary>
    public bool Available => Directory.Exists(GzctfDir) && _hostDir.Value is not null;

    private static string Key(int participationId, int challengeId) => $"{participationId}-{challengeId}";

    /// <summary>gzctf-side path of the flag file (this container's view).</summary>
    public string GzctfPath(int participationId, int challengeId) =>
        Path.Combine(GzctfDir, Key(participationId, challengeId));

    /// <summary>Host-side bind source for the child container's <c>/flag</c> mount, or null if unavailable.</summary>
    public string? BindSource(int participationId, int challengeId) =>
        _hostDir.Value is { } host ? $"{host.TrimEnd('/')}/{Key(participationId, challengeId)}" : null;

    /// <summary>True iff this team-service's container was launched with the read-only bind mount.</summary>
    public bool IsBindMounted(int participationId, int challengeId) =>
        Available && File.Exists(GzctfPath(participationId, challengeId));

    /// <summary>
    /// Create/overwrite the flag file <b>in place</b> (same inode, so the child's
    /// bind mount sees the update — never delete + recreate, that would orphan
    /// the mount). Left world-readable so the service (any uid) can read it.
    /// </summary>
    public void Write(int participationId, int challengeId, string flag)
    {
        var path = GzctfPath(participationId, challengeId);
        File.WriteAllText(path, flag);
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead); // 644
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "A&D flag mount: chmod 644 failed for {Path}", path);
        }
    }

    /// <summary>Ensure the file exists (warmup) before the container is created so the bind mounts a file, not a dir.</summary>
    public void EnsureWarmup(int participationId, int challengeId)
    {
        if (!File.Exists(GzctfPath(participationId, challengeId)))
            Write(participationId, challengeId, WarmupFlag);
    }

    /// <summary>Remove the backing file (on game-end teardown).</summary>
    public void Delete(int participationId, int challengeId)
    {
        try { File.Delete(GzctfPath(participationId, challengeId)); }
        catch (Exception e)
        {
            _logger.LogWarning(e, "A&D flag mount: delete failed for {Pid}-{Cid}", participationId, challengeId);
        }
    }

    private string? ResolveHostDir(IConfiguration config, IServiceProvider serviceProvider)
    {
        // Explicit override wins (useful when self-inspection isn't possible).
        var configured = config["Ad:Flag:HostDir"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        // Auto-derive: inspect our own container, find the mount whose
        // Destination is GzctfDir, and use its host-side Source. The child
        // container's bind source must be a HOST path (the daemon resolves it),
        // and for a named volume that is the volume's _data dir.
        try
        {
            if (serviceProvider.GetService<IContainerProvider<DockerClient, DockerMetadata>>() is not { } provider)
                return null;

            var self = Environment.GetEnvironmentVariable("HOSTNAME");
            if (string.IsNullOrEmpty(self))
                return null;

            var docker = provider.GetProvider();
            var inspect = docker.Containers.InspectContainerAsync(self).GetAwaiter().GetResult();
            var source = inspect.Mounts?
                .FirstOrDefault(m => m.Destination == GzctfDir)?.Source;

            if (!string.IsNullOrEmpty(source))
            {
                _logger.SystemLog($"A&D flag mount: host dir resolved to {source} (read-only /flag bind active)",
                    TaskStatus.Success, LogLevel.Information);
                return source;
            }

            _logger.SystemLog(
                $"A&D flag mount: no mount at {GzctfDir} — flags will be exec-planted (deletable by container-root). " +
                "Mount a volume there to enable the read-only bind.",
                TaskStatus.Pending, LogLevel.Information);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "A&D flag mount: host-dir auto-derivation failed; falling back to exec-plant");
        }

        return null;
    }
}
