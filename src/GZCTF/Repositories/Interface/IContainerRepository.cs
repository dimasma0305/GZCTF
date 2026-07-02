using GZCTF.Models.Request.Admin;

namespace GZCTF.Repositories.Interface;

public interface IContainerRepository : IRepository
{
    /// <summary>
    /// Get container by database ID
    /// </summary>
    /// <param name="guid">container ID</param>
    /// <param name="token"></param>
    /// <returns></returns>
    public Task<Container?> GetContainerById(Guid guid, CancellationToken token = default);

    /// <summary>
    /// Get container with instance info by database ID
    /// </summary>
    /// <param name="guid">container ID</param>
    /// <param name="token"></param>
    /// <returns></returns>
    public Task<Container?> GetContainerWithInstanceById(Guid guid, CancellationToken token = default);

    /// <summary>
    /// Check if the container exists by database ID
    /// </summary>
    /// <param name="guid">container ID</param>
    /// <param name="token"></param>
    /// <returns></returns>
    public Task<bool> ValidateContainer(Guid guid, CancellationToken token = default);

    /// <summary>
    /// Get all container instances
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    public Task<ContainerInstanceModel[]> GetContainerInstances(CancellationToken token = default);

    /// <summary>
    /// Get all containers that are about to be stopped
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    public Task<Container[]> GetDyingContainers(CancellationToken token = default);

    /// <summary>
    /// Every non-destroyed container's provider-side ID (Docker container ID / K8s pod
    /// &amp; service name). Used by <see cref="Services.Container.Manager.KubernetesDriftCheckService"/>
    /// to spot K8s resources the platform lost track of (crash mid-teardown, a manual
    /// <c>kubectl delete</c>, node eviction that skipped GZCTF's own cleanup path).
    /// </summary>
    public Task<HashSet<string>> GetLiveContainerIds(CancellationToken token = default);

    /// <summary>
    /// Extend container lifetime
    /// </summary>
    /// <param name="container">container</param>
    /// <param name="time">extension period</param>
    /// <param name="token"></param>
    /// <returns></returns>
    public Task ExtendLifetime(Container container, TimeSpan time, CancellationToken token = default);

    /// <summary>
    /// Destroy container and remove it from database
    /// </summary>
    /// <param name="container"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    public Task<bool> DestroyContainer(Container container, CancellationToken token = default);

    /// <summary>
    /// Get all flag strings associated with a challenge for static-flag challenges
    /// (StaticAttachment / StaticContainer). Returns the rendered flag strings only.
    /// Used by the egress flag tracer to scan packets for shared static flags.
    /// </summary>
    /// <param name="challengeId">challenge ID</param>
    /// <param name="token"></param>
    /// <returns>Distinct flag strings for the challenge, or empty if none.</returns>
    public Task<string[]> GetStaticChallengeFlags(int challengeId, CancellationToken token = default);

    /// <summary>
    /// Resolve a user's <see cref="Participation"/> ID in a specific game,
    /// or null if they are not on any team in that game. Indexed point read
    /// against the <c>UserParticipations</c> table; used by the container
    /// access logger to determine whether the connecting user is from the
    /// container-owning team or a different team.
    /// </summary>
    public Task<int?> GetUserParticipationIdInGame(Guid userId, int gameId, CancellationToken token = default);

    /// <summary>
    /// Resolve the challenge that owns a shared container (a container with no GameInstance,
    /// referenced by <c>GameChallenge.SharedContainerId</c>), or null if the container isn't a
    /// shared one. Used by the proxy to attribute traffic capture per accessing team.
    /// </summary>
    public Task<SharedContainerChallengeInfo?> GetSharedContainerChallenge(Guid containerId,
        CancellationToken token = default);

    /// <summary>
    /// True if the container is bound to a real instance/service — a jeopardy/exercise
    /// <c>GameInstance</c>/<c>ExerciseInstance</c> (by <c>ContainerId</c>), an A&amp;D
    /// <c>AdTeamService</c>, a KotH <c>KothTarget</c>, or a challenge's shared container
    /// (<c>GameChallenge.SharedContainerId</c>). Used by the admin NoInstance proxy to reject
    /// real player/team containers (the vestigial <c>Container.GameInstanceId</c> reverse-FK is
    /// never populated, so it cannot be used for this check).
    /// </summary>
    public Task<bool> IsInstanceLinkedContainer(Guid containerId, CancellationToken token = default);
}

/// <summary>Minimal challenge context for a shared container's traffic capture.</summary>
public sealed record SharedContainerChallengeInfo(int ChallengeId, int GameId, bool EnableTrafficCapture);
