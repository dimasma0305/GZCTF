using GZCTF.Repositories.Interface;
using MemoryPack;

namespace GZCTF.Services.Cache.Handlers;

/// <summary>
/// Background regenerator for the live KotH-only score timeline. Same shape
/// as <see cref="AdTimelineCacheHandler"/>; invalidated alongside the
/// scoreboard by <c>CacheHelper.FlushAdScoreboardCache</c> on round-advance /
/// submit / checker tick.
/// </summary>
public class KothTimelineCacheHandler : ICacheRequestHandler
{
    public string? CacheKey(CacheRequest request)
        => request.Params.Length switch
        {
            1 => Cache.CacheKey.KothTimeline(request.Params[0]),
            _ => null
        };

    public async Task<byte[]> Handle(AsyncServiceScope scope, CacheRequest request, CancellationToken token = default)
    {
        if (!int.TryParse(request.Params[0], out var id))
            return [];

        var repo = scope.ServiceProvider.GetRequiredService<IAdScoreboardRepository>();
        try
        {
            var timeline = await repo.GenKothTimelineAsync(id, cutoff: null, token);
            return MemoryPackSerializer.Serialize(timeline);
        }
        catch (Exception e)
        {
            scope.ServiceProvider.GetRequiredService<ILogger<KothTimelineCacheHandler>>()
                .LogErrorMessage(e, StaticLocalizer[nameof(Resources.Program.Cache_GenerationFailed), CacheKey(request)!]);
            return [];
        }
    }

    public static CacheRequest MakeCacheRequest(int id) =>
        new(Cache.CacheKey.KothTimelineBase,
            new() { SlidingExpiration = TimeSpan.FromDays(7) }, id.ToString());
}
