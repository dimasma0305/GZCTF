using System.ComponentModel.DataAnnotations;
using GZCTF.Utils;

namespace GZCTF.Models.Request.Admin;

public class AdAdvanceRoundResult
{
    public int RoundNumber { get; set; }
    public int FlagsPlanted { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
}

public class AdGameStateModel
{
    public int? CurrentRound { get; set; }
    public DateTimeOffset? RoundStartedAt { get; set; }
    public DateTimeOffset? RoundEndsAt { get; set; }
    public bool ScoringPaused { get; set; }

    /// <summary>When scoring was paused (null if running) — the UI freezes the round timer at this instant.</summary>
    public DateTimeOffset? ScoringPausedAt { get; set; }
    public List<AdChallengeStateModel> Challenges { get; set; } = [];
    public List<AdTeamRowModel> Teams { get; set; } = [];
}

public class AdChallengeStateModel
{
    public int ChallengeId { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public int TickSeconds { get; set; }
    public int FlagLifetimeTicks { get; set; }
    public int? TeamsWithLiveContainer { get; set; }
}

public class AdTeamRowModel
{
    public int ParticipationId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public List<AdTeamCellModel> Services { get; set; } = [];
}

public class AdTeamCellModel
{
    public int AdTeamServiceId { get; set; }
    public int ChallengeId { get; set; }
    public string? ContainerIp { get; set; }
    public int? ContainerPort { get; set; }

    /// <summary>Container GUID — target for the in-browser exec/shell terminal. Null if no live container.</summary>
    public Guid? ContainerGuid { get; set; }
    public string? LastCheckStatus { get; set; }

    /// <summary>Id of the most recent check result for this service — the target of a judge override. Null if never checked.</summary>
    public int? LastCheckId { get; set; }

    public string? CurrentFlag { get; set; }

    /// <summary>True iff a post-game snapshot tarball is stored for this team-service.</summary>
    public bool SnapshotAvailable { get; set; }

    /// <summary>Number of files the team changed vs the baseline image (docker diff), if captured.</summary>
    public int? ChangedFileCount { get; set; }
}

/// <summary>One filesystem change in a team's container vs the baseline image.</summary>
public class AdSnapshotChange
{
    /// <summary>Path inside the container.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>0 = modified, 1 = added, 2 = deleted (docker diff semantics).</summary>
    public int Kind { get; set; }
}

/// <summary>Response for the snapshot-changes endpoint.</summary>
public class AdSnapshotChangesModel
{
    public bool SnapshotAvailable { get; set; }

    /// <summary>True when <see cref="Changes"/> was computed from the live
    /// container on demand (mid-game) rather than from a stored post-game
    /// snapshot. On Kubernetes the live diff is mtime-based (files modified
    /// since container start), so it can't distinguish add/modify or detect
    /// deletions — all entries are reported as modified.</summary>
    public bool Live { get; set; }

    public List<AdSnapshotChange> Changes { get; set; } = [];
}

/// <summary>One captured point in a service's file-change history (for time-diffing).</summary>
public class AdSnapshotPointModel
{
    public int Id { get; set; }
    public int Round { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    /// <summary>Number of changed files at this point.</summary>
    public int FileCount { get; set; }
}

/// <summary>Diff between two capture points: which files the team touched between them.</summary>
public class AdSnapshotTimeDiffModel
{
    /// <summary>Files changed in the later point but not the earlier (new activity).</summary>
    public List<AdSnapshotChange> Added { get; set; } = [];

    /// <summary>Files changed in the earlier point but not the later (e.g. reverted).</summary>
    public List<AdSnapshotChange> Removed { get; set; } = [];
}

/// <summary>Result of spawning a throwaway inspector container.</summary>
public class AdInspectorModel
{
    /// <summary>GUID of the spawned container — feed to the in-browser shell (ContainerExecHub).</summary>
    public Guid ContainerGuid { get; set; }
}

/// <summary>One file's content, capped + binary-aware (see AdFileViewModel).</summary>
public class AdFileBlob
{
    /// <summary>Bytes returned (capped at the read limit, not the true file size when truncated).</summary>
    public long Size { get; set; }

    /// <summary>True if the file was larger than the read cap and got truncated.</summary>
    public bool Truncated { get; set; }

    /// <summary>True if the content looks binary (contains NUL) — then <see cref="Base64"/> is set instead of <see cref="Text"/>.</summary>
    public bool Binary { get; set; }

    /// <summary>UTF-8 text content (when not binary).</summary>
    public string? Text { get; set; }

    /// <summary>Base64 content (when binary).</summary>
    public string? Base64 { get; set; }
}

/// <summary>
/// Response for the per-file inspection endpoint: a changed file's current
/// content (from the running container), its baseline content (from the
/// challenge image), and a unified diff between them.
/// </summary>
public class AdFileViewModel
{
    public string Path { get; set; } = string.Empty;

    /// <summary>True when the team's container is live (so <see cref="Current"/> could be read).</summary>
    public bool ContainerRunning { get; set; }

    /// <summary>Current content from the running container; null if no live container or the file is absent.</summary>
    public AdFileBlob? Current { get; set; }

    /// <summary>Baseline content from the challenge image; null if the image lacks the file (e.g. team-added).</summary>
    public AdFileBlob? Baseline { get; set; }

    /// <summary>Unified diff (baseline → current), present only when both sides are text within the diff line cap.</summary>
    public string? UnifiedDiff { get; set; }
}

public class AdOverrideCheckModel
{
    [Required]
    public AdCheckStatus NewStatus { get; set; }

    [MaxLength(2048)]
    public string? Note { get; set; }
}
