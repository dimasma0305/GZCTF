using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using GZCTF.Models.Internal;
using GZCTF.Repositories.Interface;
using GZCTF.Services;
using GZCTF.Services.Cache;
using GZCTF.Services.Traffic;
using GZCTF.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace GZCTF.Controllers;

/// <summary>
/// Container TCP traffic proxy and logging APIs
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ProxyController(
    ILogger<ProxyController> logger,
    IDistributedCache cache,
    IOptions<ContainerProvider> provider,
    IContainerRepository containerRepository,
    TrafficRecorderRegistry trafficRegistry,
    IStringLocalizer<Program> localizer) : ControllerBase
{
    private const int BufferSize = 4096;
    private const uint ConnectionLimit = 32;

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
            TypeInfoResolver = new AppJsonSerializerContext()
        };

    private static readonly DistributedCacheEntryOptions StoreOption =
        new() { SlidingExpiration = TimeSpan.FromHours(10) };

    private static readonly DistributedCacheEntryOptions ValidOption =
        new() { SlidingExpiration = TimeSpan.FromMinutes(10) };

    private readonly bool _enablePlatformProxy =
        provider.Value.PortMappingType == ContainerPortMappingType.PlatformProxy;

    private readonly bool _enableTrafficCapture = provider.Value.EnableTrafficCapture;

    /// <summary>
    /// Proxy TCP over websocket
    /// </summary>
    /// <param name="id">Container ID</param>
    /// <param name="token"></param>
    /// <returns></returns>
    [Route("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status418ImATeapot)]
    public async Task<IActionResult> ProxyForInstance(Guid id, CancellationToken token = default)
    {
        if (!_enablePlatformProxy)
            return BadRequest(new RequestResponse(localizer[nameof(Resources.Program.Proxy_TcpDisabled)]));

        if (!await ValidateContainer(id, token))
            return NotFound(new RequestResponse(localizer[nameof(Resources.Program.Container_NotFound)],
                StatusCodes.Status404NotFound));

        if (!HttpContext.WebSockets.IsWebSocketRequest)
            return NoContent();

        var container = await containerRepository.GetContainerWithInstanceById(id, token);

        if (container is null || !container.IsProxy)
            return NotFound(new RequestResponse(localizer[nameof(Resources.Program.Container_NotFound)],
                StatusCodes.Status404NotFound));

        var ipAddress = (await Dns.GetHostAddressesAsync(container.IP, token)).FirstOrDefault();

        if (ipAddress is null)
            return BadRequest(new RequestResponse(localizer[nameof(Resources.Program.Container_AddressResolveFailed)]));

        var clientIp = HttpContext.Connection.RemoteIpAddress ?? IPAddress.Loopback;

        var gi = container.GameInstance;

        // Resolve the challenge/game context. Per-team containers get it from GameInstance;
        // a shared container (GameInstance == null) gets it from the owning GameChallenge
        // (SharedContainerId). sharedInfo stays null for a normal per-team container.
        var sharedInfo = gi is null
            ? await containerRepository.GetSharedContainerChallenge(id, token)
            : null;

        int? challengeId = gi?.ChallengeId ?? sharedInfo?.ChallengeId;
        int? gameId = gi?.Participation.GameId ?? sharedInfo?.GameId;
        var captureEnabled = gi is not null
            ? gi.Challenge.EnableTrafficCapture
            : sharedInfo?.EnableTrafficCapture ?? false;

        // Resolve the accessing user/participation once: used for the connection cap (below),
        // capture attribution (shared has no owner → the CONNECTING team), and the access log.
        Guid? accessUserId = null;
        string? accessUserName = null;
        int? accessParticipationId = null;
        var isAdmin = false;
        if (HttpContext.User?.Identity?.IsAuthenticated == true && gameId is { } gid)
        {
            var idStr = HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (Guid.TryParse(idStr, out var parsed))
            {
                accessUserId = parsed;
                accessUserName = HttpContext.User.Identity.Name;
                accessParticipationId = await containerRepository.GetUserParticipationIdInGame(parsed, gid, token);
                isAdmin = await ContextHelper.HasMonitor(HttpContext);
            }
        }

        // Enforce the connection cap only AFTER validation succeeds (the early returns above
        // must not consume a slot — only DoContainerProxy's finally releases it). Per-team
        // containers are naturally isolated (one container per team = its own 32-slot pool); a
        // SHARED container is one container for ALL teams, so scope the cap per accessing team —
        // otherwise one team can saturate the pool and DoS the shared service for everyone.
        var key = sharedInfo is not null && accessParticipationId is { } capPid
            ? $"{CacheKey.ConnectionCount(id)}:{capPid}"
            : CacheKey.ConnectionCount(id);
        if (!await IncreaseConnectionCount(key))
            return BadRequest(
                new RequestResponse(localizer[nameof(Resources.Program.Container_ConnectionLimitExceeded)]));

        TrafficWriter? writer = null;
        IPEndPoint client;
        IPEndPoint target;
        int realRemotePort;
        try
        {
        if (_enableTrafficCapture && captureEnabled && challengeId is { } cid)
        {
            // Per-team → the owning team; shared → the accessing team (no owner). When a shared
            // connection can't be attributed to a participant (e.g. an admin preview), skip it.
            var captureParticipationId = gi?.ParticipationId ?? accessParticipationId;
            if (captureParticipationId is { } pid)
            {
                // Dynamic challenges carry a per-team flag on GameInstance.FlagContext.
                // StaticContainer (per-team or shared) shares one static flag from GameChallenge.Flags.
                string? flag = gi?.FlagContext?.Flag;
                var isStaticFlag = false;
                var isStaticType = gi is null || gi.Challenge.Type == ChallengeType.StaticContainer;
                if (string.IsNullOrEmpty(flag) && isStaticType)
                {
                    var staticFlags = await containerRepository.GetStaticChallengeFlags(cid, token);
                    flag = staticFlags.FirstOrDefault();
                    isStaticFlag = !string.IsNullOrEmpty(flag);
                }

                var descriptor = new TrafficRecorderDescriptor(
                    ContainerId: id,
                    ChallengeId: cid,
                    ParticipationId: pid,
                    GameId: gameId ?? 0,
                    Flag: flag,
                    IsStaticFlag: isStaticFlag,
                    Metadata: container.GenerateMetadata(JsonOptions),
                    ConnectionId: HttpContext.Connection.Id,
                    RemoteIpAddress: HttpContext.Connection.RemoteIpAddress,
                    // Per-team containers run flag-egress scanning; shared containers (no
                    // owning team, one shared static flag) get the raw pcap only.
                    ScanFlagEgress: gi is not null);
                writer = trafficRegistry.AcquireWriter(descriptor);
            }
        }

        realRemotePort = HttpContext.Connection.RemotePort;
        var clientPort = writer?.Sequence ?? realRemotePort;

        client = new(clientIp, clientPort);
        target = new(ipAddress, container.Port);

        // Record the proxy access for cross-team detection. Per-team only: a shared container
        // has no owning team, so owner-vs-accessor comparison is meaningless. Purely additive —
        // failure must never block the proxy (try/catch).
        if (gi is not null)
        try
        {
            var accessLogger = HttpContext.RequestServices.GetRequiredService<IContainerAccessLogger>();

            var userAgent = HttpContext.Request.Headers.UserAgent.ToString();
            if (userAgent.Length > 512) userAgent = userAgent[..512];

            await accessLogger.LogAccess(new ContainerAccessContext(
                ContainerId: id,
                ChallengeId: gi.ChallengeId,
                ContainerOwnerParticipationId: gi.ParticipationId,
                GameId: gi.Participation.GameId,
                AccessingUserId: accessUserId,
                AccessingUserName: accessUserName,
                AccessingParticipationId: accessParticipationId,
                RemoteIp: clientIp.ToString(),
                UserAgent: string.IsNullOrEmpty(userAgent) ? null : userAgent,
                IsAdmin: isAdmin,
                ConnectedAtUtc: DateTimeOffset.UtcNow), token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ContainerAccessLogger failed for container {Id}", id);
        }
        }
        catch
        {
            // We incremented the connection count above but never reached
            // DoContainerProxy (whose finally is the only decrement on the happy path).
            // A throw in the traffic-writer setup (e.g. AcquireWriter under disk
            // pressure, or the static-flag DB query) would otherwise leak the slot and
            // eventually lock the owner out of their own container. DoContainerProxy is
            // OUTSIDE this try, so its own decrement never double-fires.
            await DecreaseConnectionCount(key);
            throw;
        }

        return await DoContainerProxy(id, key, client, target, writer, realRemotePort, token);
    }

    /// <summary>
    /// Proxy TCP over websocket for admins
    /// </summary>
    /// <param name="id">Test container ID</param>
    /// <param name="token"></param>
    /// <returns></returns>
    [Route("NoInst/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(RequestResponse), StatusCodes.Status418ImATeapot)]
    [SuppressMessage("ReSharper", "RouteTemplates.ParameterTypeCanBeMadeStricter")]
    public async Task<IActionResult> ProxyForNoInstance(Guid id, CancellationToken token = default)
    {
        if (!_enablePlatformProxy)
            return BadRequest(new RequestResponse(localizer[nameof(Resources.Program.Proxy_TcpDisabled)]));

        if (!await ValidateContainer(id, token))
            return NotFound(new RequestResponse(localizer[nameof(Resources.Program.Container_NotFound)],
                StatusCodes.Status404NotFound));

        if (!HttpContext.WebSockets.IsWebSocketRequest)
            return NoContent();

        var container = await containerRepository.GetContainerById(id, token);

        if (container is null || container.GameInstanceId is not null || !container.IsProxy)
            return NotFound(new RequestResponse(localizer[nameof(Resources.Program.Container_NotFound)],
                StatusCodes.Status404NotFound));

        var ipAddress = (await Dns.GetHostAddressesAsync(container.IP, token)).FirstOrDefault();

        if (ipAddress is null)
            return BadRequest(new RequestResponse(localizer[nameof(Resources.Program.Container_AddressResolveFailed)]));

        var clientIp = HttpContext.Connection.RemoteIpAddress ?? IPAddress.Loopback;
        var clientPort = HttpContext.Connection.RemotePort;

        IPEndPoint client = new(clientIp, clientPort);
        IPEndPoint target = new(ipAddress, container.Port);

        return await DoContainerProxy(id, CacheKey.ConnectionCount(id), client, target, null, client.Port, token);
    }

    private async Task<IActionResult> DoContainerProxy(Guid id, string connectionKey, IPEndPoint client,
        IPEndPoint target, TrafficWriter? writer, int realClientPort, CancellationToken token = default)
    {
        using var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        CaptureNetworkStream? stream = null;

        try
        {
            try
            {
                await socket.ConnectAsync(target, token);

                if (!socket.Connected)
                    throw new SocketException((int)SocketError.NotConnected);

                stream = new CaptureNetworkStream(socket, writer, client, target);
            }
            catch (SocketException e)
            {
                logger.SystemLog(
                    StaticLocalizer[nameof(Resources.Program.Proxy_ContainerConnectionFailedLog),
                        e.SocketErrorCode,
                        $"{target.Address}:{target.Port}"],
                    TaskStatus.Failed, LogLevel.Debug);

                return RequestResponse.Result(
                    localizer[nameof(Resources.Program.Proxy_ContainerConnectionFailed), e.SocketErrorCode],
                    StatusCodes.Status418ImATeapot);
            }

            using var ws = await HttpContext.WebSockets.AcceptWebSocketAsync();

            try
            {
                var (tx, rx) = await RunProxy(stream, ws, token);
                LogProxyResult(id, new IPEndPoint(client.Address, realClientPort), target, tx, rx);
            }
            catch (Exception e)
            {
                logger.LogErrorMessage(e, StaticLocalizer[nameof(Resources.Program.Proxy_Error)]);
            }
        }
        finally
        {
            if (stream is not null)
                await stream.DisposeAsync();
            else
                writer?.Dispose();

            await DecreaseConnectionCount(connectionKey);
        }

        return new EmptyResult();
    }

    private void LogProxyResult(Guid id, IPEndPoint client, IPEndPoint target, ulong tx, ulong rx)
    {
        var shortId = id.ToString("N")[..12];
        var clientAddress = client.Address.IsIPv4MappedToIPv6 ? client.Address.MapToIPv4() : client.Address;
        var targetAddress = target.Address.IsIPv4MappedToIPv6 ? target.Address.MapToIPv4() : target.Address;

        logger.SystemLog($"[{shortId}] {clientAddress} -> {targetAddress}:{target.Port}, tx {tx}, rx {rx}",
            TaskStatus.Success, LogLevel.Debug);
    }

    /// <summary>
    /// Proxy TCP traffic using websocket
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="ws"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    private static async Task<(ulong, ulong)> RunProxy(CaptureNetworkStream stream, WebSocket ws,
        CancellationToken token = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);

        cts.CancelAfter(TimeSpan.FromMinutes(30));

        var ct = cts.Token;
        ulong tx = 0, rx = 0;

        var sender = Task.Run(async () =>
        {
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                while (true)
                {
                    var status = await ws.ReceiveAsync(buffer, ct);
                    if (status.CloseStatus.HasValue)
                        break;
                    if (status.Count <= 0)
                        continue;

                    tx += (ulong)status.Count;
                    var memory = buffer.AsMemory(0, status.Count);
                    await stream.WriteAsync(memory, ct);
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }, ct);

        var receiver = Task.Run(async () =>
        {
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                while (true)
                {
                    var count = await stream.ReadAsync(buffer, ct);
                    if (count == 0)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.Empty, null, ct);
                        break;
                    }

                    rx += (ulong)count;
                    var memory = buffer.AsMemory(0, count);
                    await ws.SendAsync(memory, WebSocketMessageType.Binary, true, ct);
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }, ct);

        await Task.WhenAny(sender, receiver);
        await cts.CancelAsync();
        await Task.WhenAll(sender, receiver);

        return (tx, rx);
    }

    /// <summary>
    /// Validate container existence
    /// </summary>
    /// <param name="id">Container ID</param>
    /// <param name="token"></param>
    /// <returns></returns>
    private async Task<bool> ValidateContainer(Guid id, CancellationToken token = default)
    {
        var key = CacheKey.ConnectionCount(id);
        var bytes = await cache.GetAsync(key, token);

        // avoid DoS attack with cache -1
        if (bytes is not null)
            return BitConverter.ToInt32(bytes) >= 0;

        var valid = await containerRepository.ValidateContainer(id, token);

        await cache.SetAsync(key, BitConverter.GetBytes(valid ? 0 : -1), ValidOption, token);

        return valid;
    }

    /// <summary>
    /// Increase Fetch-Add operation for container TCP connection count
    /// </summary>
    /// <param name="key">Cache key</param>
    /// <returns></returns>
    // Striped locks serializing the connection-count read-modify-write. The backing
    // IDistributedCache has no atomic increment, so without this N concurrent WebSocket
    // upgrades all read the same pre-increment count and blow past the cap (socket/FD/
    // recorder exhaustion DoS). A FIXED-size array (not a per-GUID dictionary) keeps the
    // lock set bounded regardless of container churn — no per-container entry to leak
    // over a long event. Distinct containers may share a stripe (harmless: the cap check
    // is keyed on the cache value; the lock only serializes the RMW). Correct for the
    // single-instance deployment; a horizontally-scaled one would also need Redis INCR.
    private static readonly SemaphoreSlim[] ConnCountLocks =
        Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private static SemaphoreSlim ConnCountLock(string key) =>
        ConnCountLocks[(int)((uint)key.GetHashCode() % (uint)ConnCountLocks.Length)];

    private async Task<bool> IncreaseConnectionCount(string key)
    {
        var gate = ConnCountLock(key);
        await gate.WaitAsync();
        try
        {
            var bytes = await cache.GetAsync(key);

            if (bytes is null)
                return false;

            var count = BitConverter.ToInt32(bytes);

            // count < 0 is the invalid-container sentinel (ValidateContainer); >= cap
            // rejects (was `>`, an off-by-one that allowed 33 concurrent for a cap of 32).
            if (count < 0 || count >= ConnectionLimit)
                return false;

            await cache.SetAsync(key, BitConverter.GetBytes(count + 1), StoreOption);

            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Implement decrease operation for container TCP connection count
    /// </summary>
    /// <param name="key">Cache key</param>
    /// <returns></returns>
    private async Task DecreaseConnectionCount(string key)
    {
        // Same per-container lock as IncreaseConnectionCount so a decrement can't
        // race an increment and lose an update (which would otherwise drift the
        // count and eventually lock the owner out of their own container).
        var gate = ConnCountLock(key);
        await gate.WaitAsync();
        try
        {
            var bytes = await cache.GetAsync(key);

            if (bytes is null)
                return;

            var count = BitConverter.ToInt32(bytes);

            if (count > 1)
                await cache.SetAsync(key, BitConverter.GetBytes(count - 1), StoreOption);
            else
                await cache.SetAsync(key, BitConverter.GetBytes(0), ValidOption);
        }
        finally
        {
            gate.Release();
        }
    }
}
