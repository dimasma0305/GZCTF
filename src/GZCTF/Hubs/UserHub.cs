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
        // Don't let an anonymous client subscribe to a Hidden (draft) game's notice
        // feed. Monitors may (they manage the draft).
        var isMonitor = await ContextHelper.HasMonitor(context);
        if (!await gameRepository.HasGameAsync(gId, allowHidden: isMonitor))
        {
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();

        await Groups.AddToGroupAsync(Context.ConnectionId, $"Game_{gId}");
    }
}
