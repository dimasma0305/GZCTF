using GZCTF.Repositories.Interface;
using MemoryPack;

namespace GZCTF.Services.Cache.Handlers;

/// <summary>
/// Background regenerator for the live KotH-only scoreboard. Same shape as
/// <see cref="AdScoreboardCacheHandler"/>; invalidated alongside the A&amp;D board
/// by <c>CacheHelper.FlushAdScoreboardCache</c> on round-advance / submit /
/// checker tick so viewers never trigger the full aggregation themselves.
/// </summary>
public class KothScoreboardCacheHandler : ICacheRequestHandler
{
    public string? CacheKey(CacheRequest request)
        => request.Params.Length switch
        {
            1 => Cache.CacheKey.KothScoreboard(request.Params[0]),
            _ => null
        };

    public async Task<byte[]> Handle(AsyncServiceScope scope, CacheRequest request, CancellationToken token = default)
    {
        if (!int.TryParse(request.Params[0], out var id))
            return [];

        var repo = scope.ServiceProvider.GetRequiredService<IAdScoreboardRepository>();
        try
        {
            var board = await repo.GenKothScoreboardAsync(id, cutoff: null, token);
            return MemoryPackSerializer.Serialize(board);
        }
        catch (Exception e)
        {
            scope.ServiceProvider.GetRequiredService<ILogger<KothScoreboardCacheHandler>>()
                .LogErrorMessage(e, StaticLocalizer[nameof(Resources.Program.Cache_GenerationFailed), CacheKey(request)!]);
            return [];
        }
    }

    public static CacheRequest MakeCacheRequest(int id) =>
        new(Cache.CacheKey.KothScoreboardBase,
            new() { SlidingExpiration = TimeSpan.FromDays(7) }, id.ToString());
}
