namespace GZCTF.Models.Request.Game;

/// <summary>
/// Public "team patched their service" event, broadcast on the unauthenticated
/// attack feed to the per-game battle map when a team's A&amp;D service container
/// is detected to have changed files since the previous snapshot (i.e. they
/// edited / hardened their service). Fired by <see cref="GZCTF.Services.AdSnapshotService"/>
/// at the moment a new changed-file manifest is recorded, so the visualization can
/// show a "PATCHED" effect on the defending team's node. Best-effort and purely
/// cosmetic — it signals "their container's files changed", not that a specific bug
/// was actually fixed.
/// </summary>
/// <param name="TeamName">Defending team's display name — the node to flash on the client.</param>
/// <param name="ChallengeId">The patched service's challenge id — stable node key.</param>
/// <param name="ChallengeTitle">The patched service's title.</param>
/// <param name="Round">The round the change was detected in.</param>
/// <param name="ChangeCount">Number of changed paths in the container diff (rough patch size).</param>
public record PatchEvent(
    string TeamName,
    int ChallengeId,
    string ChallengeTitle,
    int Round,
    int ChangeCount);
