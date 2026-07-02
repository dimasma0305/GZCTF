using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using GZCTF.Models.Request.Game;
using GZCTF.Repositories.Interface;
using GZCTF.Utils;
using StackExchange.Redis;

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
/// <see cref="AttackEvent"/>), <c>"koth"</c> (a hill control change — same shape as
/// <see cref="KothControlEvent"/>), or <c>"patch"</c> (a team's service files changed —
/// same shape as <see cref="PatchEvent"/>). Same public-but-not-Hidden gate as the SignalR hub
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

    // When Redis is configured, every published frame is fanned out over this pub/sub
    // channel so a raw-WS client connected to replica B still sees an attack processed on
    // replica A. Null (and everything falls back to direct local dispatch) on a single
    // instance / when Redis isn't configured.
    private readonly IConnectionMultiplexer? _redis;
    private static readonly RedisChannel StreamChannel =
        RedisChannel.Literal("gzctf:attackstream");

    public AttackStreamService(IConnectionMultiplexer? redis = null)
    {
        _redis = redis;
        if (_redis is null)
            return;

        // Subscribe once; StackExchange.Redis re-establishes the subscription automatically
        // across reconnects. EVERY replica (the publisher included) receives each frame here
        // and dispatches to its OWN local sockets exactly once — dispatch is done solely in
        // this handler, never also inline in Publish, so the origin replica doesn't
        // double-deliver its own frames.
        try
        {
            _redis.GetSubscriber().Subscribe(StreamChannel, (_, value) =>
            {
                if (value.IsNullOrEmpty)
                    return;
                try
                {
                    var env = JsonSerializer.Deserialize<StreamEnvelope>((string)value!, JsonOpts);
                    if (env is not null)
                        DispatchLocal(env.GameId, env.Frame);
                }
                catch
                {
                    // Malformed cross-replica frame — ignore rather than fault the subscriber.
                }
            });
        }
        catch
        {
            // Redis unreachable at startup — the multiplexer will retry the subscription on
            // reconnect. Local publishes still work (see Publish's fallback).
        }
    }

    /// <summary>Fan a flag-submission event out to this game's raw-WS subscribers.</summary>
    public void PublishAttack(int gameId, AttackEvent evt) => Publish(gameId, "attack", evt);

    /// <summary>Fan a KotH control-change event out to this game's raw-WS subscribers.</summary>
    public void PublishKoth(int gameId, KothControlEvent evt) => Publish(gameId, "koth", evt);

    /// <summary>Fan a "team patched their service" event out to this game's raw-WS subscribers.</summary>
    public void PublishPatch(int gameId, PatchEvent evt) => Publish(gameId, "patch", evt);

    private void Publish(int gameId, string kind, object payload)
    {
        // Serialize once; send the same string to every subscriber. The "kind" tag is
        // merged into the event object so a client reads one flat JSON per frame.
        var node = JsonSerializer.SerializeToNode(payload, JsonOpts)!.AsObject();
        node["kind"] = kind;
        var json = node.ToJsonString(JsonOpts);

        if (_redis is not null)
        {
            // Fan out via Redis; the subscription handler on every replica (this one included)
            // does the actual local dispatch, so we do NOT also dispatch inline here. Note we
            // can't early-out on an empty *local* bucket like the direct path below — another
            // replica may have subscribers even when this one doesn't. Fire-and-forget: a raw-WS
            // feed frame isn't worth blocking the caller (a submission / KotH broadcast) on.
            try
            {
                var envelope = JsonSerializer.Serialize(new StreamEnvelope(gameId, json), JsonOpts);
                _ = _redis.GetSubscriber().PublishAsync(StreamChannel, envelope, CommandFlags.FireAndForget);
            }
            catch
            {
                // Redis blip — fall back to at least serving this replica's own subscribers so a
                // single-instance-shaped outage degrades gracefully rather than dropping frames.
                DispatchLocal(gameId, json);
            }
            return;
        }

        DispatchLocal(gameId, json);
    }

    private void DispatchLocal(int gameId, string json)
    {
        if (!_subs.TryGetValue(gameId, out var conns) || conns.IsEmpty)
            return;

        foreach (var ch in conns.Values)
            ch.Writer.TryWrite(json); // never blocks the broadcaster — DropOldest on a full queue
    }

    private sealed record StreamEnvelope(int GameId, string Frame);

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
            new { kind = "hello", game = gameId, events = new[] { "attack", "koth", "patch" } }, JsonOpts));

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
            // Intentionally keep the (possibly empty) per-game bucket. Removing it here races with
            // the GetOrAdd in HandleWebSocketAsync: a client connecting exactly as the last one
            // leaves could land its channel in a bucket that this thread then drops from _subs,
            // orphaning it from Publish. Buckets are keyed by gameId, so the leftover is bounded by
            // game count (negligible), and Publish early-outs on an empty bucket.
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
