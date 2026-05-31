using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using GZCTF.Models.Request.Game;
using GZCTF.Repositories.Interface;
using GZCTF.Utils;

namespace GZCTF.Services;

/// <summary>
/// Plain-WebSocket mirror of the SignalR <see cref="GZCTF.Hubs.AttackHub"/> live feed,
/// for participants who want to tap a game's attack / King-of-the-Hill events from a
/// script or bot <b>without</b> speaking the SignalR negotiate + framing protocol.
///
/// <para>Endpoint: <c>GET /hub/attack/ws?game={id}</c> (WebSocket upgrade). The server
/// pushes <b>one JSON object per text frame</b>; the client sends nothing. Every frame
/// has a <c>kind</c> field: <c>"hello"</c> (once, on connect), <c>"ping"</c> (keepalive,
/// ~every 25s), <c>"attack"</c> (a flag submission — same shape as
/// <see cref="AttackEvent"/>), or <c>"koth"</c> (a hill control change — same shape as
/// <see cref="KothControlEvent"/>). Same public-but-not-Hidden gate as the SignalR hub
/// (Hidden/draft games are monitor-only).</para>
///
/// <para>The SignalR broadcast sites call <see cref="PublishAttack"/> /
/// <see cref="PublishKoth"/> immediately after their hub send, so the two transports
/// always carry the same events. Singleton.</para>
/// </summary>
public sealed class AttackStreamService
{
    // gameId → (connectionId → outbound queue). A per-connection bounded channel keeps a
    // single writer per socket (WebSocket forbids concurrent SendAsync) and lets a slow
    // client be dropped (DropOldest) instead of stalling the broadcaster.
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<Guid, Channel<string>>> _subs = new();

    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Fan a flag-submission event out to this game's raw-WS subscribers.</summary>
    public void PublishAttack(int gameId, AttackEvent evt) => Publish(gameId, "attack", evt);

    /// <summary>Fan a KotH control-change event out to this game's raw-WS subscribers.</summary>
    public void PublishKoth(int gameId, KothControlEvent evt) => Publish(gameId, "koth", evt);

    private void Publish(int gameId, string kind, object payload)
    {
        if (!_subs.TryGetValue(gameId, out var conns) || conns.IsEmpty)
            return;

        // Serialize once; send the same string to every subscriber. The "kind" tag is
        // merged into the event object so a client reads one flat JSON per frame.
        var node = JsonSerializer.SerializeToNode(payload, JsonOpts)!.AsObject();
        node["kind"] = kind;
        var json = node.ToJsonString(JsonOpts);

        foreach (var ch in conns.Values)
            ch.Writer.TryWrite(json); // never blocks the broadcaster — DropOldest on a full queue
    }

    /// <summary>
    /// Endpoint handler for <c>GET /hub/attack/ws?game={id}</c>. Validates the upgrade +
    /// game, registers the socket, streams events until the client disconnects.
    /// </summary>
    public async Task HandleWebSocketAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!ctx.Request.Query.TryGetValue("game", out var raw) || !int.TryParse(raw, out var gameId))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Same gate as AttackHub: the game must exist, and Hidden (draft) games are only
        // visible to monitors (so an anonymous client can't tap a draft game's feed).
        var gameRepo = ctx.RequestServices.GetRequiredService<IGameRepository>();
        var isMonitor = await ContextHelper.HasMonitor(ctx);
        if (!await gameRepo.HasGameAsync(gameId, allowHidden: isMonitor))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var id = Guid.NewGuid();
        var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

        var conns = _subs.GetOrAdd(gameId, static _ => new ConcurrentDictionary<Guid, Channel<string>>());
        conns[id] = ch;

        // Greeting so a client knows it connected and what frame kinds to expect.
        ch.Writer.TryWrite(JsonSerializer.Serialize(
            new { kind = "hello", game = gameId, events = new[] { "attack", "koth" } }, JsonOpts));

        try
        {
            var writer = WriterLoopAsync(socket, ch, ctx.RequestAborted);
            await ReceiverLoopAsync(socket, ctx.RequestAborted);
            ch.Writer.TryComplete();
            await writer;
        }
        catch
        {
            // Connection died mid-stream — fall through to cleanup.
        }
        finally
        {
            conns.TryRemove(id, out _);
            if (conns.IsEmpty)
                _subs.TryRemove(gameId, out _);
        }
    }

    // The single sender for a socket (no concurrent SendAsync). Drains the queue and, when
    // idle, emits a keepalive ping so a reverse proxy (traefik) can't idle-drop the socket.
    private static async Task WriterLoopAsync(WebSocket socket, Channel<string> ch, CancellationToken token)
    {
        var keepalive = TimeSpan.FromSeconds(25);
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            string msg;
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
            idle.CancelAfter(keepalive);
            try
            {
                msg = await ch.Reader.ReadAsync(idle.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                msg = "{\"kind\":\"ping\"}"; // idle → keepalive
            }
            catch
            {
                break; // channel completed (socket closing) or the request aborted
            }

            try
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, token);
            }
            catch
            {
                break;
            }
        }
    }

    // Consume-only feed: we ignore any client input, but must observe the close handshake
    // so the connection (and its slot) is cleaned up promptly.
    private static async Task ReceiverLoopAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[512];
        try
        {
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    break;
                }
            }
        }
        catch
        {
            // closed / aborted
        }
    }
}
