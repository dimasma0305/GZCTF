namespace GZCTF.Models.Request.Game;

/// <summary>
/// Public King-of-the-Hill control-change event, broadcast on the unauthenticated
/// AttackHub to the per-game attack animation page when the team holding a hill
/// changes (a takeover, or a hill going uncontrolled). Lets the visualization render
/// hills as objective nodes that recolor to the new holder and fire a beam from the
/// capturing team — the KotH analogue of an A&amp;D capture <see cref="AttackEvent"/>.
/// </summary>
/// <param name="ChallengeId">The hill's challenge id — stable node key on the client.</param>
/// <param name="ChallengeTitle">The hill's title (e.g. "Crypto Hill").</param>
/// <param name="Round">The round this control verdict was scored for.</param>
/// <param name="HolderTeamName">
/// New holder's display name, or null when the hill went uncontrolled (no valid token
/// in the marker this tick).
/// </param>
/// <param name="HolderTeamAvatar">Relative URL to the new holder's avatar (nullable).</param>
/// <param name="PreviousTeamName">
/// The team that held the hill the previous tick, or null if it was uncontrolled. The
/// client aims the takeover beam from the new holder at this team's node (falling back
/// to the hill node itself when null).
/// </param>
/// <param name="Status">Functional probe verdict on the hill — Ok / Mumble / Offline / InternalError.</param>
public record KothControlEvent(
    int ChallengeId,
    string ChallengeTitle,
    int Round,
    string? HolderTeamName,
    string? HolderTeamAvatar,
    string? PreviousTeamName,
    string Status);
