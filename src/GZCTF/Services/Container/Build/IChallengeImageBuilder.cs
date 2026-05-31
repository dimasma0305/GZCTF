namespace GZCTF.Services.Container.Build;

/// <summary>
/// Inputs for a one-shot challenge image build. The context directory
/// is what gets tar'd up and shipped to <c>docker build</c>; the
/// dockerfile path is relative to that directory.
/// </summary>
public sealed record ChallengeBuildRequest(
    int ChallengeId,
    int GameId,
    string ChallengeSlug,
    string ContextDir,
    string Dockerfile,
    ChallengeBuildKind Kind = ChallengeBuildKind.Challenge);

/// <summary>
/// Outcome of a single build. On success <see cref="ImageTag"/> is what
/// the caller assigns to <c>GameChallenge.ContainerImage</c>; on
/// failure <see cref="LogTail"/> carries the last few KiB of build
/// output for the admin audit modal.
/// </summary>
public sealed record ChallengeBuildResult(
    bool Success,
    string? ImageTag,
    string? Digest,
    string LogTail,
    string? ErrorMessage);

/// <summary>
/// Builds an OCI image from a directory of files (the extracted
/// challenge package) and tags it for the local runtime to pick up.
/// Docker impl uses <c>BuildImageFromDockerfileAsync</c> against the
/// mounted socket; the Kubernetes impl is a placeholder that surfaces
/// "not supported in kubernetes runtime" until kaniko / BuildKit-in-pod
/// is wired up.
/// </summary>
public interface IChallengeImageBuilder
{
    /// <summary>
    /// Run a single image build.
    /// </summary>
    /// <param name="req">Build inputs (context dir + dockerfile + slug).</param>
    /// <param name="token">Cancel token plumbed to the underlying docker
    /// call.</param>
    /// <param name="onProgress">Optional sink invoked once per line of
    /// build output. The implementation buffers these locally too —
    /// this callback exists so callers can stream the live log to
    /// somewhere visible (e.g. update the challenge row periodically so
    /// the admin UI can watch in real time). May be called from a
    /// non-UI thread; the sink must be threadsafe.</param>
    Task<ChallengeBuildResult> BuildAsync(
        ChallengeBuildRequest req,
        CancellationToken token,
        Action<string>? onProgress = null);

    /// <summary>
    /// Self-heal: ensure a previously-built local image still exists, rebuilding
    /// it from a persisted build context if it has gone missing (e.g. an ad-hoc
    /// <c>docker image prune -a</c> deleted the local-only checker image — these
    /// have no long-running container holding them, so they vanish and every
    /// A&amp;D/KotH check then reports InternalError on the failed pull).
    /// </summary>
    /// <param name="imageTag">The expected local image tag, i.e. a
    /// <c>gzctf-auto/{game}/{slug}:{digest}</c> reference.</param>
    /// <param name="token">Cancellation token.</param>
    /// <returns><c>true</c> if the image is present (already, or after a
    /// successful rebuild); <c>false</c> if it could not be restored (not a
    /// local autobuilt tag, no persisted context, or the rebuild failed).
    /// Never throws for the missing-context case — callers treat false as
    /// "leave it to the operator / re-import".</returns>
    Task<bool> TryRestoreImageAsync(string imageTag, CancellationToken token);
}
