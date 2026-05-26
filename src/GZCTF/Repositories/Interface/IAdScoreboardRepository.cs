using GZCTF.Models.Request.Game;
using GZCTF.Models.Response.Admin;

namespace GZCTF.Repositories.Interface;

/// <summary>
/// Builds + caches the Attack &amp; Defense scoreboard and score timeline.
/// Mirrors how <see cref="IGameRepository"/> caches the jeopardy board: the
/// expensive aggregation runs once per round (or submit) into the distributed
/// cache via <c>CacheMaker</c>, instead of on every viewer request.
/// </summary>
public interface IAdScoreboardRepository : IRepository
{
    /// <summary>Build the scoreboard from the DB (uncached). <paramref name="cutoff"/>
    /// null = live; non-null = ICPC-freeze snapshot as of that instant.</summary>
    Task<AdScoreboardModel> GenScoreboardAsync(int gameId, DateTimeOffset? cutoff, CancellationToken token = default);

    /// <summary>Get the scoreboard, building + caching on miss (blocks the first caller).</summary>
    Task<AdScoreboardModel> GetScoreboardAsync(int gameId, DateTimeOffset? cutoff, CancellationToken token = default);

    /// <summary>Non-blocking cache read; null on miss.</summary>
    Task<AdScoreboardModel?> TryGetScoreboardAsync(int gameId, bool frozen, CancellationToken token = default);

    /// <summary>Build the score timeline from the DB (uncached, downsampled).</summary>
    Task<AdScoreTimelineModel> GenTimelineAsync(int gameId, DateTimeOffset? cutoff, CancellationToken token = default);

    /// <summary>Get the timeline, building + caching on miss.</summary>
    Task<AdScoreTimelineModel> GetTimelineAsync(int gameId, DateTimeOffset? cutoff, CancellationToken token = default);

    /// <summary>Non-blocking cache read; null on miss.</summary>
    Task<AdScoreTimelineModel?> TryGetTimelineAsync(int gameId, bool frozen, CancellationToken token = default);
}
