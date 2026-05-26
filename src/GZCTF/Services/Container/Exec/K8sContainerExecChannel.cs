using System.Net.WebSockets;
using System.Text.Json;
using GZCTF.Services.Container.Provider;
using k8s;

namespace GZCTF.Services.Container.Exec;

/// <summary>
/// Kubernetes in-browser shell, backed by the pod <c>exec</c> streaming API
/// (the same channel <c>kubectl exec</c> uses). Opens a TTY against the
/// challenge container and bridges its multiplexed stdin/stdout/resize channels
/// to <see cref="IExecSession"/> for <c>ContainerExecHub</c>.
///
/// <para>The pod name, the challenge container name, and
/// <see cref="Models.Data.Container.ContainerId"/> are all the same value
/// (see <c>KubernetesManager</c>), so we exec into <c>ContainerId</c> directly —
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

        var demux = await _client.MuxedStreamNamespacedPodExecAsync(
            name: container.ContainerId,
            @namespace: _namespace,
            command: [safeShell],
            container: container.ContainerId,
            tty: true,
            cancellationToken: token);
        demux.Start();

        logger.LogInformation("K8s exec opened: pod {Id}, shell {Shell}", container.LogId, safeShell);
        return new K8sExecSession(demux, logger);
    }

    sealed class K8sExecSession : IExecSession
    {
        private readonly IStreamDemuxer _demux;
        private readonly Stream _io;     // read stdout (stderr is merged under TTY), write stdin
        private readonly Stream _resize; // write {"Width":..,"Height":..} resize frames
        private readonly ILogger _logger;

        public K8sExecSession(IStreamDemuxer demux, ILogger logger)
        {
            _demux = demux;
            _logger = logger;
            _io = demux.GetStream(ChannelIndex.StdOut, ChannelIndex.StdIn);
            _resize = demux.GetStream(null, ChannelIndex.Resize);
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token) =>
            await _io.ReadAsync(buffer, token); // 0 = EOF (shell exited / channel closed)

        public ValueTask WriteAsync(ReadOnlyMemory<byte> chunk, CancellationToken token) =>
            _io.WriteAsync(chunk, token);

        public async Task ResizeAsync(uint cols, uint rows, CancellationToken token)
        {
            // remotecommand resize frame on channel 4: JSON {"Width":cols,"Height":rows}.
            var frame = JsonSerializer.SerializeToUtf8Bytes(new TerminalSize { Width = cols, Height = rows });
            await _resize.WriteAsync(frame, token);
            await _resize.FlushAsync(token);
        }

        public async ValueTask DisposeAsync()
        {
            try { await _io.FlushAsync(CancellationToken.None); } catch { /* closing */ }
            // StreamDemuxer.Dispose tears down the underlying WebSocket.
            try { _demux.Dispose(); } catch { /* already gone */ }
        }

        private sealed class TerminalSize
        {
            public uint Width { get; init; }
            public uint Height { get; init; }
        }
    }
}
