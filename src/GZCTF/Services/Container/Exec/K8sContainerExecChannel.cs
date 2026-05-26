using System.Net.WebSockets;
using System.Text.Json;
using GZCTF.Services.Container.Provider;
using k8s;

namespace GZCTF.Services.Container.Exec;

/// <summary>
/// Kubernetes in-browser shell, backed by the pod <c>exec</c> WebSocket (the
/// same channel <c>kubectl exec</c> uses). Opens a TTY against the challenge
/// container and bridges its multiplexed channels to <see cref="IExecSession"/>
/// for <c>ContainerExecHub</c>.
///
/// <para>We frame the SPDY/WebSocket channels ourselves rather than going
/// through <c>StreamDemuxer</c>: its <c>MuxedStream</c> only implements a
/// blocking synchronous <c>Write</c> (no real <c>WriteAsync</c>) and sends
/// partial frames, which wedged stdin so keystrokes never reached the pod.
/// Each message is <c>[channel byte][payload]</c> — channel 0 stdin, 1 stdout,
/// 2 stderr, 3 error, 4 resize.</para>
///
/// <para>The pod name, the challenge container name, and
/// <see cref="Models.Data.Container.ContainerId"/> are all the same value (see
/// <c>KubernetesManager</c>), so we exec into <c>ContainerId</c> directly —
/// never the flag-writer sidecar.</para>
/// </summary>
public sealed class K8sContainerExecChannel(
    IContainerProvider<Kubernetes, KubernetesMetadata> provider,
    ILogger<K8sContainerExecChannel> logger) : IContainerExecChannel
{
    private readonly Kubernetes _client = provider.GetProvider();
    private readonly string _namespace = provider.GetMetadata().Config.Namespace;

    public async Task<IExecSession> OpenAsync(Models.Data.Container container, string shell, CancellationToken token)
    {
        var safeShell = string.Equals(shell, "bash", StringComparison.OrdinalIgnoreCase) ? "bash" : "sh";

        var ws = await _client.WebSocketNamespacedPodExecAsync(
            name: container.ContainerId,
            @namespace: _namespace,
            command: new[] { safeShell },
            container: container.ContainerId,
            stderr: true, stdin: true, stdout: true, tty: true,
            cancellationToken: token);

        logger.LogInformation("K8s exec opened: pod {Id}, shell {Shell}", container.LogId, safeShell);
        return new K8sExecSession(ws);
    }

    sealed class K8sExecSession : IExecSession
    {
        // k8s remotecommand channels.
        private const byte ChStdin = 0, ChStdout = 1, ChStderr = 2, ChResize = 4;

        private readonly WebSocket _ws;
        // Serializes sends — a WebSocket forbids concurrent SendAsync, and we
        // send from both WriteAsync (keystrokes) and ResizeAsync.
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        // Receive buffer + leftover bookkeeping: a single output frame can be
        // larger than the hub's read buffer, so we only pull the next frame
        // once the current one is fully drained (no overwrite-before-read).
        private readonly byte[] _recv = new byte[16 * 1024];
        private int _off;
        private int _len;
        private int _cont = -1; // channel of an in-progress (fragmented) message, else -1

        public K8sExecSession(WebSocket ws) => _ws = ws;

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token)
        {
            while (_len == 0)
            {
                WebSocketReceiveResult r;
                try { r = await _ws.ReceiveAsync(new ArraySegment<byte>(_recv), token); }
                catch (WebSocketException) { return 0; }
                catch (OperationCanceledException) { return 0; }

                if (r.MessageType == WebSocketMessageType.Close || r.Count == 0)
                    return 0;

                int ch, off, len;
                if (_cont < 0) { ch = _recv[0]; off = 1; len = r.Count - 1; } // new message: first byte = channel
                else { ch = _cont; off = 0; len = r.Count; }                  // continuation frame
                _cont = r.EndOfMessage ? -1 : ch;

                // Surface stdout/stderr; skip error/status (3) and empty frames.
                if (len > 0 && ch is ChStdout or ChStderr) { _off = off; _len = len; }
            }

            var n = Math.Min(_len, buffer.Length);
            new ReadOnlyMemory<byte>(_recv, _off, n).CopyTo(buffer);
            _off += n;
            _len -= n;
            return n;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> chunk, CancellationToken token) =>
            SendFramedAsync(ChStdin, chunk, token);

        public async Task ResizeAsync(uint cols, uint rows, CancellationToken token)
        {
            // remotecommand resize frame: JSON {"Width":cols,"Height":rows}.
            var json = JsonSerializer.SerializeToUtf8Bytes(new TerminalSize { Width = cols, Height = rows });
            await SendFramedAsync(ChResize, json, token);
        }

        private async ValueTask SendFramedAsync(byte channel, ReadOnlyMemory<byte> payload, CancellationToken token)
        {
            var frame = new byte[payload.Length + 1];
            frame[0] = channel;
            payload.CopyTo(frame.AsMemory(1));

            await _sendLock.WaitAsync(token);
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary,
                        endOfMessage: true, token);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
            }
            catch { /* already closing / gone */ }
            _ws.Dispose();
            _sendLock.Dispose();
        }

        private sealed class TerminalSize
        {
            public uint Width { get; init; }
            public uint Height { get; init; }
        }
    }
}
