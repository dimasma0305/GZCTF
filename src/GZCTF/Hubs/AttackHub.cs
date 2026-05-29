using System.Diagnostics.CodeAnalysis;
using GZCTF.Hubs.Clients;
using GZCTF.Repositories.Interface;
using GZCTF.Utils;
using Microsoft.AspNetCore.SignalR;

namespace GZCTF.Hubs;

/// <summary>
/// Public SignalR hub for the per-game attack animation page.
/// No authentication is required; clients only have to supply a valid game id.
/// Mirrors <see cref="MonitorHub"/> but without the admin/monitor gate.
/// </summary>
[ExcludeFromCodeCoverage]
public class AttackHub : Hub<IAttackClient>
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
        // Don't let an anonymous client subscribe to a Hidden (draft) game's live
        // attack feed (team names, avatars, challenge titles). Monitors may.
        var isMonitor = await ContextHelper.HasMonitor(context);
        if (!await gameRepository.HasGameAsync(gId, allowHidden: isMonitor))
        {
            Context.Abort();
            return;
        }

        await base.OnConnectedAsync();

        await Groups.AddToGroupAsync(Context.ConnectionId, $"AttackGame_{gId}");
    }
}
