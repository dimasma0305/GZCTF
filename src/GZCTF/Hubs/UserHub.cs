using System.Diagnostics.CodeAnalysis;
using GZCTF.Hubs.Clients;
using GZCTF.Repositories.Interface;
using GZCTF.Utils;
using Microsoft.AspNetCore.SignalR;

namespace GZCTF.Hubs;

[ExcludeFromCodeCoverage]
public class UserHub : Hub<IUserClient>
{
    public override async Task OnConnectedAsync()
    {
        var context = Context.GetHttpContext();

        if (context is null
            || !context.Request.Query.TryGetValue("game", out var gameId)
            || !int.TryParse(gameId, out var gId))
        {
            Context.Abort();
            return;
        }

        var gameRepository = context.RequestServices.GetRequiredService<IGameRepository>();
        var isMonitor = await ContextHelper.HasMonitor(context);
        var info = await gameRepository.GetGameHubInfoAsync(gId);
        // Reject unknown games; Hidden (draft) games for non-monitors; and — mirroring
        // the REST GameController.Notices gate — non-monitor connections before the
        // game starts, so pre-start broadcast notices don't reach clients early.
        if (info is null
            || (!isMonitor && info.Value.Hidden)
            || (!isMonitor && DateTimeOffset.UtcNow < info.Value.StartTimeUtc))
        {
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();

        await Groups.AddToGroupAsync(Context.ConnectionId, $"Game_{gId}");
    }
}
