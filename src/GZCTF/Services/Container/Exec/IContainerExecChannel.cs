namespace GZCTF.Services.Container.Exec;

/// <summary>
/// Bidirectional stream over a running container — read from the
/// container's stdout / stderr, write to its stdin, resize its pty.
/// Backed by Docker's <c>exec attach</c> in the Docker runtime and by the
/// pod <c>exec</c> streaming API in the Kubernetes runtime.
/// </summary>
public interface IExecSession : IAsyncDisposable
{
    /// <summary>Read the next chunk of output (raw bytes, TTY-multiplexed).</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token);

    /// <summary>Write input bytes (typically a single keystroke).</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> chunk, CancellationToken token);

    /// <summary>Resize the pseudo-tty.</summary>
    Task ResizeAsync(uint cols, uint rows, CancellationToken token);
}

/// <summary>
/// Per-runtime exec channel factory. The Docker impl uses
/// <c>ExecCreateContainerAsync</c> + <c>StartAndAttachContainerExecAsync</c>
/// against the mounted socket; the Kubernetes impl uses
/// <c>MuxedStreamNamespacedPodExecAsync</c> (the pod exec streaming API).
/// </summary>
public interface IContainerExecChannel
{
    Task<IExecSession> OpenAsync(Models.Data.Container container, string shell, CancellationToken token);
}
