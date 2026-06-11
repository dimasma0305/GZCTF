using GZCTF.Models.Internal;

namespace GZCTF.Repositories.Interface;

public interface IGameInstanceRepository : IRepository
{
    /// <summary>
    /// Get or create a game instance for a team and challenge
    /// </summary>
    /// <param name="team">Team</param>
    /// <param name="challengeId">Challenge id</param>
    /// <param name="token"></param>
    /// <returns></returns>
    public Task<GameInstance?> GetInstance(Participation team, int challengeId, CancellationToken token = default);

    /// <summary>
    /// Get a game instance for submission, with minimal includes
    /// </summary>
    /// <param name="team">Team</param>
    /// <param name="challengeId">Challenge id</param>
    /// <param name="token"></param>
    /// <returns></returns>
    public Task<GameInstance?> GetInstanceForSubmission(Participation team, int challengeId,
        CancellationToken token = default);

    /// <summary>
    /// Verify the answer of a submission
    /// </summary>
    /// <param name="submission">Submission</param>
    /// <param name="token"></param>
    /// <returns></returns>
    public Task<VerifyResult> VerifyAnswer(Submission submission, CancellationToken token = default);

    /// <summary>
    /// Check if the submission is a cheat
    /// </summary>
    /// <param name="submission">Submission</param>
    /// <param name="token"></param>
    /// <returns></returns>
    public Task<CheatCheckInfo> CheckCheat(Submission submission, CancellationToken token = default);

    /// <summary>
    /// Create a container for a game instance
    /// </summary>
    /// <param name="team">Team info</param>
    /// <param name="game">Game</param>
    /// <param name="user">User</param>
    /// <param name="gameInstance">Instance</param>
    /// <param name="token"></param>
    /// <returns></returns>
    public Task<TaskResult<Container>> CreateContainer(GameInstance gameInstance, Team team, UserInfo user,
        Game game, CancellationToken token = default);

    /// <summary>
    /// Get the existing shared container for a challenge, or null if none is alive.
    /// </summary>
    /// <param name="challenge">Challenge</param>
    /// <param name="token"></param>
    public Task<Container?> GetSharedContainer(GameChallenge challenge, CancellationToken token = default);

    /// <summary>
    /// Get-or-create the single shared container for a <see cref="GameChallenge.UsesSharedContainer"/>
    /// challenge. Returns the existing one (lifetime refreshed) or creates a new one. Concurrency-safe.
    /// </summary>
    /// <param name="challenge">Challenge</param>
    /// <param name="game">Game</param>
    /// <param name="user">Requesting user (for the event log)</param>
    /// <param name="token"></param>
    public Task<TaskResult<Container>> GetOrCreateSharedContainer(GameChallenge challenge, Game game, UserInfo user,
        CancellationToken token = default);

    /// <summary>
    /// Destroy all containers of a challenge
    /// </summary>
    /// <param name="challenge">Challenge</param>
    /// <param name="token"></param>
    /// <returns></returns>
    public Task DestroyAllContainers(GameChallenge challenge, CancellationToken token = default);
}
