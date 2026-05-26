namespace GZCTF.Models.Internal;

public class ContainerConfig
{
    /// <summary>
    /// Container image
    /// </summary>
    public string Image { get; set; } = string.Empty;

    /// <summary>
    /// Team ID
    /// </summary>
    public string TeamId { get; set; } = string.Empty;

    /// <summary>
    /// Challenge ID
    /// </summary>
    public int ChallengeId { get; set; }

    /// <summary>
    /// Game ID, null for exercise containers
    /// </summary>
    public int? GameId { get; set; }

    /// <summary>
    /// User ID
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Port to be exposed by the container
    /// </summary>
    public int ExposedPort { get; set; }

    /// <summary>
    /// Flag text. For static / dynamic-container challenges this is the
    /// per-team flag set at container create time. For A&amp;D this is the
    /// FIRST round's flag — the env var is immutable for the life of the
    /// process, so subsequent ticks update <see cref="FlagFilePath"/>
    /// instead. Operator's challenge code should prefer reading the file.
    /// </summary>
    public string? Flag { get; set; } = string.Empty;

    /// <summary>
    /// In-container path of the per-tick flag. Exposed to the running container
    /// as the <c>GZCTF_FLAG_FILE</c> env var so challenge code knows where to
    /// look without hard-coding the path. Only set for A&amp;D containers; null
    /// for jeopardy + exercise.
    /// </summary>
    public string? FlagFilePath { get; set; }

    /// <summary>
    /// Host path of a file to bind-mount <b>read-only</b> at
    /// <see cref="FlagFilePath"/>. When set, the flag is served from this
    /// host-backed file (rewritten in place each tick) instead of a
    /// <c>docker exec</c> write — making <c>/flag</c> undeletable/untamperable
    /// by container-root. Null = legacy exec plant. A&amp;D only.
    /// </summary>
    public string? FlagBindSource { get; set; }

    /// <summary>
    /// Full URL the Kubernetes flag-writer sidecar polls to pull the current
    /// A&amp;D flag (virtual nodes can't <c>exec</c>, so flags are pulled, not
    /// pushed). Includes the per-(team, challenge) HMAC token. Set only for A&amp;D
    /// containers on the K8s provider; null for Docker (which uses the read-only
    /// bind mount) and for jeopardy/exercise. When set, <see cref="FlagFilePath"/>
    /// is the path inside the read-only volume the challenge reads.
    /// </summary>
    public string? FlagPullUrl { get; set; }

    /// <summary>
    /// Whether to record traffic
    /// </summary>
    public bool EnableTrafficCapture { get; set; }

    /// <summary>
    /// Memory limit (MB)
    /// </summary>
    public int MemoryLimit { get; set; } = 64;

    /// <summary>
    /// CPU limit (0.1 CPUs)
    /// </summary>
    public int CPUCount { get; set; } = 1;

    /// <summary>
    /// Storage write limit
    /// </summary>
    public int StorageLimit { get; set; } = 256;

    /// <summary>
    /// Container network mode
    /// </summary>
    public NetworkMode NetworkMode { get; set; } = NetworkMode.Open;
}
