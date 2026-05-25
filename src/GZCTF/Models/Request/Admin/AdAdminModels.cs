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
    public List<AdSnapshotChange> Changes { get; set; } = [];
}

public class AdOverrideCheckModel
{
    [Required]
    public AdCheckStatus NewStatus { get; set; }

    [MaxLength(2048)]
    public string? Note { get; set; }
}
