namespace GZCTF.Models.Request.Game;

/// <summary>
/// Public, IP-free snapshot of one King-of-the-Hill hill, used to SEED the
/// unauthenticated attack animation page on load (the live updates then arrive as
/// <see cref="KothControlEvent"/> over the AttackHub). Deliberately omits the hill's
/// container IP/port — those are gameplay-sensitive and only exposed to authenticated
/// participants via <c>AdGameController.KothHills</c>; a spectator only needs the hill
/// name, who holds it, and its functional status.
/// </summary>
/// <param name="ChallengeId">The hill's challenge id — stable node key on the client.</param>
/// <param name="Title">The hill's title.</param>
/// <param name="HolderTeamName">Current holder's display name, or null if uncontrolled.</param>
/// <param name="HolderTeamAvatar">Current holder's avatar URL (nullable).</param>
/// <param name="Status">Last functional verdict — Ok / Mumble / Offline / InternalError / null.</param>
public record KothHillPublicModel(
    int ChallengeId,
    string Title,
    string? HolderTeamName,
    string? HolderTeamAvatar,
    string? Status);
