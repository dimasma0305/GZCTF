namespace GZCTF.Models.Request.Admin;

/// <summary>
/// Container instance information (Admin)
/// </summary>
public class ContainerInstanceModel
{
    /// <summary>
    /// Team
    /// </summary>
    public TeamModel? Team { get; set; }

    /// <summary>
    /// Challenge
    /// </summary>
    public ChallengeModel? Challenge { get; set; }

    /// <summary>
    /// Container image
    /// </summary>
    public string Image { get; set; } = string.Empty;

    /// <summary>
    /// Container database ID
    /// </summary>
    public Guid ContainerGuid { get; set; }

    /// <summary>
    /// Container ID
    /// </summary>
    public string ContainerId { get; set; } = string.Empty;

    /// <summary>
    /// Container creation time
    /// </summary>
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Expected container stop time
    /// </summary>
    public DateTimeOffset ExpectStopAt { get; set; } = DateTimeOffset.UtcNow + TimeSpan.FromHours(2);

    /// <summary>
    /// Access IP
    /// </summary>
    public string IP { get; set; } = string.Empty;

    /// <summary>
    /// Access port
    /// </summary>
    public int Port { get; set; }

    internal static ContainerInstanceModel FromContainer(
        Container container, GZCTF.Models.Data.AdTeamService? adService = null,
        GZCTF.Models.Data.GameChallenge? sharedChallenge = null)
    {
        // Prefer the GameInstance side (jeopardy + exercise); fall back to the A&D
        // AdTeamService, then to a shared (challenge-owned) container's challenge. Callers
        // from the admin list pass adService / sharedChallenge for the GameInstance-null rows
        // so A&D and shared containers also surface their challenge (shared has no single team).
        var team = container.GameInstance?.Participation.Team
                   ?? adService?.Participation.Team;
        var chal = container.GameInstance?.Challenge
                   ?? adService?.Challenge
                   ?? sharedChallenge;

        var model = new ContainerInstanceModel
        {
            Image = container.Image,
            ContainerGuid = container.Id,
            ContainerId = container.ContainerId,
            StartedAt = container.StartedAt,
            ExpectStopAt = container.ExpectStopAt,
            // fallback to host if public is null
            IP = container.PublicIP ?? container.IP,
            Port = container.PublicPort ?? container.Port
        };

        // Set each independently: a shared container has a challenge but no single team.
        if (chal is not null)
            model.Challenge = ChallengeModel.FromChallenge(chal);
        if (team is not null)
            model.Team = TeamModel.FromTeam(team);

        return model;
    }
}
