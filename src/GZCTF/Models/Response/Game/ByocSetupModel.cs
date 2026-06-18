namespace GZCTF.Models.Response.Game;

/// <summary>
/// Self-hosted ("bring your own container") setup bundle for one team + one A&amp;D
/// challenge: a ready-to-run docker-compose the team drops next to their own
/// service, plus the facts the UI shows alongside it.
/// </summary>
public class ByocSetupModel
{
    /// <summary>A complete docker-compose.yml (agent image + tunnel URL + token baked in).</summary>
    public string Compose { get; set; } = string.Empty;

    /// <summary>
    /// The outbound WebSocket URL the agent dials. Contains the team's scoped
    /// tunnel token — treat it like a credential.
    /// </summary>
    public string TunnelUrl { get; set; } = string.Empty;

    /// <summary>The port the team's service must listen on (the relay forwards to it).</summary>
    public int ServicePort { get; set; }

    /// <summary>The agent image the compose references (organizer-published / pullable).</summary>
    public string AgentImage { get; set; } = string.Empty;
}
