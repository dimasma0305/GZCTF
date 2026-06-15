using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;
using GZCTF.Models.Internal;
using GZCTF.Services.Container.Provider;
using Microsoft.Extensions.Options;

namespace GZCTF.Services.Container.Build;

/// <summary>
/// Builds challenge images via Docker.DotNet against the mounted
/// <c>docker.sock</c>. Tags are deterministic
/// (<c>gzctf-auto/{gameId}/{slug}:{contextSha[..12]}</c>) so re-imports
/// with no source changes reuse the existing image without rebuilding.
///
/// No registry push — the same daemon GZCTF talks to is the one the
/// runner uses, so locally tagged images are immediately available.
/// </summary>
public sealed class DockerChallengeImageBuilder(
    IContainerProvider<DockerClient, DockerMetadata> provider,
    IOptionsMonitor<BuildRegistryConfig> registryConfig,
    IConfiguration configuration,
    ILogger<DockerChallengeImageBuilder> logger) : IChallengeImageBuilder
{
    private readonly DockerClient _client = provider.GetProvider();
    private readonly byte[] _xorKey = configuration["XorKey"]?.ToUTF8Bytes() ?? [];
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PushTimeout = TimeSpan.FromMinutes(10);
    private const int LogTailBytes = 32 * 1024;

    // Fixed per-entry tar metadata so the context tar is byte-stable for identical content
    // (deterministic content-hash tag — see BuildAsync). A fixed, non-zero date avoids any
    // "implausibly old timestamp" tar warnings that a 1970 epoch can trigger.
    private static readonly DateTimeOffset BuildEntryMTime =
        new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const UnixFileMode BuildEntryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.GroupRead | UnixFileMode.OtherRead; // 0644

    public async Task<ChallengeBuildResult> BuildAsync(
        ChallengeBuildRequest req,
        CancellationToken token,
        Action<string>? onProgress = null)
    {
        // Disambiguate the image repo by challenge id, not just the title slug. Two
        // challenges in one game whose titles normalize to the same slug (e.g. "Web 1" and
        // "Web-1" both -> "web-1") would otherwise share a gzctf-auto repo, and one build's
        // tag-cleanup would delete the sibling's image — silently breaking a live challenge.
        // The id makes the repo unique per challenge; this slug flows to the build tag, the
        // push repository, and the cleanup target, so all three stay consistent.
        // Checker builds get a distinct repo suffix so the challenge's service image
        // and its checker image never share a gzctf-auto repo (a shared repo's
        // tag-cleanup would delete the sibling).
        var kindSuffix = req.Kind == ChallengeBuildKind.Checker ? "-checker" : string.Empty;
        var slug = $"{req.ChallengeId}-{NormalizeSlug(req.ChallengeSlug)}{kindSuffix}";
        var contextTar = Path.Combine(Path.GetTempPath(), $"gzctf-build-{Guid.NewGuid():N}.tar.gz");

        try
        {
            // Write the build context to a gzip'd tar and get back a STABLE content hash
            // (see WriteContextTarAsync). Identical challenge content always yields the same
            // digest → the same gzctf-auto tag → a re-import reuses the existing image
            // instead of minting a fresh tag every time (the churn that left multiple images
            // per challenge on disk).
            var digest = await WriteContextTarAsync(req.ContextDir, contextTar, token);
            var tag = $"gzctf-auto/{req.GameId}/{slug}:{digest[..12]}";

            // Fast path: if the local tag already exists, skip the
            // docker build. But still drop through to the push step if
            // the registry is configured — the registry may not yet
            // have this digest even though the local daemon does.
            string? cachedImageId = null;
            try
            {
                var existing = await _client.Images.InspectImageAsync(tag, token);
                cachedImageId = existing.ID;
                logger.LogInformation("BuildAsync: image {Tag} already exists locally (digest {Id})", tag, existing.ID);
            }
            catch (DockerImageNotFoundException) { /* not cached */ }
            catch (DockerApiException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { /* not cached */ }

            // Cache hit + no push needed → return immediately.
            if (cachedImageId is not null && !registryConfig.CurrentValue.IsConfigured)
            {
                PersistBuildContext(req.GameId, slug, digest[..12], contextTar, req.Dockerfile);
                return new ChallengeBuildResult(true, tag, cachedImageId, "(cached)", null);
            }

            using var timeout = new CancellationTokenSource(BuildTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);

            var logTail = new StringBuilder();
            string? lastError = null;
            string? imageId = cachedImageId;

            // Skip the docker build call when the image is already
            // cached locally — the source context hash is deterministic
            // so an existing tag is by-definition up-to-date.
            if (cachedImageId is null)
            {
                var progress = new Progress<JSONMessage>(msg =>
                {
                    if (!string.IsNullOrEmpty(msg.Stream))
                    {
                        AppendTail(logTail, msg.Stream);
                        try { onProgress?.Invoke(msg.Stream); } catch { /* sink errors must not break the build */ }
                    }
                    if (!string.IsNullOrEmpty(msg.Status))
                    {
                        var line = msg.Status + "\n";
                        AppendTail(logTail, line);
                        try { onProgress?.Invoke(line); } catch { /* sink errors must not break the build */ }
                    }
                    if (msg.Error is { Message: { Length: > 0 } em })
                        lastError = em;
                });

                await using (var contextStream = File.OpenRead(contextTar))
                {
                    await _client.Images.BuildImageFromDockerfileAsync(
                        new ImageBuildParameters
                        {
                            Dockerfile = req.Dockerfile,
                            Tags = [tag],
                            Remove = true,
                            ForceRemove = true,
                            NoCache = false,
                            // Mark platform-built images so a host-side
                            // `docker image prune -af --filter label!=org.gzctf.keep=true`
                            // won't delete them. Critical for A&D checker images, which
                            // have no long-running container holding them (spawned per
                            // tick) and would otherwise be pruned → checks InternalError.
                            Labels = new Dictionary<string, string> { ["org.gzctf.keep"] = "true" },
                        },
                        contextStream,
                        authConfigs: null,
                        headers: null,
                        progress: progress,
                        linked.Token);
                }

                if (lastError is not null)
                {
                    logger.LogWarning("BuildAsync: build failed for {Tag}: {Err}", tag, lastError);
                    return new ChallengeBuildResult(false, null, null, logTail.ToString(), lastError);
                }

                // Confirm the image actually exists and grab a digest.
                try
                {
                    var inspect = await _client.Images.InspectImageAsync(tag, token);
                    imageId = inspect.ID;
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "BuildAsync: image {Tag} inspect failed after build", tag);
                }
            }
            else
            {
                AppendTail(logTail, $"[build] image already cached locally as {tag}\n");
            }

            // Optional registry push. The local tag is what's stored
            // on the daemon; if the operator configured a push target
            // (BuildRegistryConfig.PushOnBuild), we retag with the
            // registry prefix and push. The returned ImageTag is the
            // registry tag — that's what the challenge row's
            // ContainerImage gets set to, and the runner pulls it from
            // there.
            var reg = registryConfig.CurrentValue;
            string returnedTag = tag;
            if (reg.IsConfigured)
            {
                var pushed = await TryPushAsync(tag, req.GameId, slug, digest[..12],
                    reg, logTail, linked.Token);
                if (pushed is null)
                {
                    // TryPushAsync wrote the failure into logTail before
                    // returning null; surface it as a build failure so the
                    // operator sees what went wrong.
                    return new ChallengeBuildResult(false, null, null, logTail.ToString(),
                        "Registry push failed — see build log for details.");
                }
                returnedTag = pushed;
            }

            // Auto-cleanup after a successful build: drop any older
            // tags of this same challenge (different content SHAs from
            // previous edits) plus the dangling images + build cache
            // they leave behind. Best-effort — a cleanup failure must
            // never flip a successful build to Failed.
            await CleanupAfterBuildAsync(req.GameId, slug, digest[..12], logTail, token);

            // Stash the context so this image can be rebuilt byte-for-byte if it
            // later gets pruned out from under a running game (self-heal).
            PersistBuildContext(req.GameId, slug, digest[..12], contextTar, req.Dockerfile);

            return new ChallengeBuildResult(true, returnedTag, imageId, logTail.ToString(), null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new ChallengeBuildResult(false, null, null, "(cancelled)", "Build cancelled by host.");
        }
        catch (Exception e)
        {
            logger.LogError(e, "DockerChallengeImageBuilder: build failed");
            return new ChallengeBuildResult(false, null, null, e.Message, e.Message);
        }
        finally
        {
            try { File.Delete(contextTar); } catch { /* best effort */ }
        }
    }

    // ── Self-heal: persisted build contexts ──────────────────────────────────
    // Local-only autobuilt images (gzctf-auto/...) have no long-running container
    // holding them, so an ad-hoc `docker image prune -a` that doesn't honour the
    // org.gzctf.keep label (the daily cron does) can delete a checker image
    // mid-game → every check InternalErrors on the failed pull. We stash the exact
    // context tarball + dockerfile under the data dir so the image can be rebuilt
    // byte-for-byte (same content → same deterministic tag) on demand, with no
    // re-import. AdCheckerImageHealService drives the restore.

    private static string StoreRoot => Path.Combine(PathHelper.Base, "build-contexts");

    private static bool IsSafeSegment(string s) =>
        s.Length is > 0 and <= 128
        && s != "." && s != ".."
        && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>Map a <c>gzctf-auto/{game}/{slug}:{digest}</c> tag to its persisted
    /// context paths. Returns false for anything not a well-formed local tag.</summary>
    private static bool TryResolveStorePaths(string tag, out string tarPath, out string dfPath)
    {
        tarPath = dfPath = string.Empty;
        if (string.IsNullOrEmpty(tag)) return false;
        var colon = tag.LastIndexOf(':');
        if (colon <= 0 || colon == tag.Length - 1) return false;
        var digest = tag[(colon + 1)..];
        var parts = tag[..colon].Split('/');           // gzctf-auto / {gameId} / {slug}
        if (parts.Length != 3 || parts[0] != "gzctf-auto") return false;
        var (gameId, slug) = (parts[1], parts[2]);
        if (!IsSafeSegment(gameId) || !IsSafeSegment(slug) || !IsSafeSegment(digest)) return false;
        var dir = Path.Combine(StoreRoot, gameId, slug);
        tarPath = Path.Combine(dir, digest + ".tar.gz");
        dfPath = Path.Combine(dir, digest + ".df");
        return true;
    }

    /// <summary>Best-effort: stash the context tar + dockerfile so the image can be
    /// rebuilt later if pruned. Keeps only the current digest per slug.</summary>
    private void PersistBuildContext(int gameId, string slug, string digest, string contextTar, string dockerfile)
    {
        try
        {
            if (!IsSafeSegment(gameId.ToString()) || !IsSafeSegment(slug) || !IsSafeSegment(digest)) return;
            var dir = Path.Combine(StoreRoot, gameId.ToString(), slug);
            Directory.CreateDirectory(dir);
            File.Copy(contextTar, Path.Combine(dir, digest + ".tar.gz"), overwrite: true);
            File.WriteAllText(Path.Combine(dir, digest + ".df"),
                string.IsNullOrWhiteSpace(dockerfile) ? "Dockerfile" : dockerfile);
            // Only the current digest is useful — drop older stashes for this slug.
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (!Path.GetFileName(f).StartsWith(digest, StringComparison.Ordinal))
                    try { File.Delete(f); } catch { /* best effort */ }
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "PersistBuildContext: failed to stash context for {Slug}", slug);
        }
    }

    public async Task<bool> TryRestoreImageAsync(string imageTag, CancellationToken token)
    {
        // Only our local autobuilt images are restorable; registry refs are pulled.
        if (string.IsNullOrWhiteSpace(imageTag) ||
            !imageTag.StartsWith("gzctf-auto/", StringComparison.Ordinal))
            return false;

        // Already present? The common case every reconcile tick — cheap no-op.
        try
        {
            await _client.Images.InspectImageAsync(imageTag, token);
            return true;
        }
        catch (DockerImageNotFoundException) { /* missing → rebuild below */ }
        catch (DockerApiException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { /* missing */ }

        if (!TryResolveStorePaths(imageTag, out var tarPath, out var dfPath)
            || !File.Exists(tarPath) || !File.Exists(dfPath))
        {
            logger.LogWarning(
                "Self-heal: image {Tag} is missing and no persisted build context exists — re-import the challenge to rebuild it",
                imageTag);
            return false;
        }

        var dockerfile = (await File.ReadAllTextAsync(dfPath, token)).Trim();
        if (dockerfile.Length == 0) dockerfile = "Dockerfile";

        logger.SystemLog($"Self-heal: rebuilding missing image {imageTag} from persisted context",
            TaskStatus.Pending, LogLevel.Information);

        var logTail = new StringBuilder();
        var ok = await BuildImageFromTarAsync(tarPath, dockerfile, imageTag, logTail, token);
        if (ok)
            logger.SystemLog($"Self-heal: restored image {imageTag}", TaskStatus.Success, LogLevel.Information);
        else
            logger.LogWarning("Self-heal: rebuild of {Tag} failed: {Log}", imageTag,
                logTail.Length > 0 ? logTail.ToString() : "(no output)");
        return ok;
    }

    public async Task<int> DeleteGameImagesAsync(int gameId, CancellationToken token)
    {
        // Match every local tag for this game: bare gzctf-auto/{gameId}/{slug}:...
        // and the registry-prefixed {ns}/gzctf-auto/{gameId}/{slug}:... form, for
        // both the challenge and its -checker image. The trailing slash keeps
        // game 19 from also matching games 1 or 190.
        var marker = $"gzctf-auto/{gameId}/";

        int removed = 0;
        try
        {
            var images = await _client.Images.ListImagesAsync(
                new ImagesListParameters { All = false }, token);

            foreach (var img in images)
            {
                if (img.RepoTags is null) continue;
                foreach (var rt in img.RepoTags)
                {
                    if (!rt.Contains(marker, StringComparison.Ordinal)) continue;
                    try
                    {
                        // Force so a shared base layer / multi-tag image still gets
                        // this tag removed; the game is gone so nothing should hold it.
                        await _client.Images.DeleteImageAsync(rt,
                            new ImageDeleteParameters { Force = true, NoPrune = false }, token);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning("Game-image cleanup: failed to delete {Tag}: {Err}", rt, ex.Message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Game-image cleanup: listing images for game {GameId} failed: {Err}", gameId, ex.Message);
        }

        if (removed > 0)
            logger.SystemLog($"Removed {removed} autobuilt image tag(s) for deleted game {gameId}",
                TaskStatus.Success, LogLevel.Information);
        return removed;
    }

    /// <summary>Build a fixed tag from an already-tar'd context, skipping the
    /// content-hash step (the persisted tar IS the content and its tag is known).
    /// Mirrors the docker build call in <see cref="BuildAsync"/>.</summary>
    private async Task<bool> BuildImageFromTarAsync(
        string contextTarPath, string dockerfile, string tag, StringBuilder logTail, CancellationToken token)
    {
        string? lastError = null;
        var progress = new Progress<JSONMessage>(msg =>
        {
            if (!string.IsNullOrEmpty(msg.Stream)) AppendTail(logTail, msg.Stream);
            if (!string.IsNullOrEmpty(msg.Status)) AppendTail(logTail, msg.Status + "\n");
            if (msg.Error is { Message: { Length: > 0 } em }) lastError = em;
        });

        using var timeout = new CancellationTokenSource(BuildTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);

        await using (var contextStream = File.OpenRead(contextTarPath))
        {
            await _client.Images.BuildImageFromDockerfileAsync(
                new ImageBuildParameters
                {
                    Dockerfile = dockerfile,
                    Tags = [tag],
                    Remove = true,
                    ForceRemove = true,
                    NoCache = false,
                    Labels = new Dictionary<string, string> { ["org.gzctf.keep"] = "true" },
                },
                contextStream, authConfigs: null, headers: null, progress: progress, linked.Token);
        }

        if (lastError is not null)
        {
            AppendTail(logTail, $"[restore] build error: {lastError}\n");
            return false;
        }
        try { await _client.Images.InspectImageAsync(tag, token); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Retag the local image with the registry prefix and push.
    /// Returns the registry tag on success, or null on failure (with
    /// the failure reason already appended to <paramref name="logTail"/>).
    /// </summary>
    /// <param name="localTag">The just-built local tag, e.g.
    /// <c>gzctf-auto/8/tower-of-babel:abc123def456</c>.</param>
    /// <param name="gameId">Owning game id (for tag composition).</param>
    /// <param name="slug">Normalized challenge slug.</param>
    /// <param name="digest">Short content SHA (12 hex chars).</param>
    /// <param name="reg">Live registry config — already validated via
    /// <see cref="BuildRegistryConfig.IsConfigured"/> by the caller.</param>
    private async Task<string?> TryPushAsync(
        string localTag, int gameId, string slug, string digest,
        BuildRegistryConfig reg, StringBuilder logTail, CancellationToken token)
    {
        // Compose the registry tag. Examples:
        //   ghcr.io/myorg/gzctf-auto/8/tower-of-babel:abc123def456
        //   registry.local:5000/gzctf-auto/8/tower-of-babel:abc123def456
        var server = reg.Server!.Trim().TrimEnd('/');
        var ns = string.IsNullOrWhiteSpace(reg.Namespace) ? null : reg.Namespace.Trim().Trim('/');
        // OCI/Docker reference grammar: only the DOMAIN (host) may be mixed-case; every PATH
        // component MUST be lowercase, else the daemon's reference parser rejects the tag with
        // "repository name must be lowercase" before any network call. GitHub org/user names (the
        // typical ghcr.io namespace) are case-preserving and commonly mixed-case, so lowercase the
        // whole path (server/host left as-is). slug is already slugified; gameId is numeric.
        var path = (ns is null
            ? $"gzctf-auto/{gameId}/{slug}"
            : $"{ns}/gzctf-auto/{gameId}/{slug}").ToLowerInvariant();
        var repository = $"{server}/{path}";
        var registryTag = $"{repository}:{digest}";

        AppendTail(logTail, $"\n[push] retagging {localTag} → {registryTag}\n");

        try
        {
            await _client.Images.TagImageAsync(localTag,
                new ImageTagParameters { RepositoryName = repository, Tag = digest },
                token);
        }
        catch (Exception ex)
        {
            AppendTail(logTail, $"[push] tag failed: {ex.Message}\n");
            logger.LogWarning(ex, "Registry push: tag failed {From} → {To}", localTag, registryTag);
            return null;
        }

        AuthConfig? auth = null;
        if (!string.IsNullOrEmpty(reg.Username))
        {
            auth = new AuthConfig
            {
                ServerAddress = server,
                Username = reg.Username,
                Password = DecryptPassword(reg.Password),
            };
        }

        AppendTail(logTail, $"[push] pushing to {server}…\n");
        using var pushTimeout = new CancellationTokenSource(PushTimeout);
        using var linkedPush = CancellationTokenSource.CreateLinkedTokenSource(token, pushTimeout.Token);

        // Daemon delivers push failures (DNS unresolvable, auth
        // rejected, etc.) via JSONMessage.Error rather than as an
        // exception from PushImageAsync. Capture them in the progress
        // sink and bail at the end if any were reported.
        string? pushError = null;
        try
        {
            var pushProgress = new Progress<JSONMessage>(msg =>
            {
                if (!string.IsNullOrEmpty(msg.Status))
                    AppendTail(logTail, $"[push] {msg.Status}{(string.IsNullOrEmpty(msg.Progress?.Current.ToString()) ? "" : " " + msg.Progress?.Current)}\n");
                if (msg.Error is { Message: { Length: > 0 } em })
                {
                    AppendTail(logTail, $"[push] error: {em}\n");
                    pushError ??= em;
                }
            });
            await _client.Images.PushImageAsync(
                repository,
                new ImagePushParameters { Tag = digest },
                auth,
                pushProgress,
                linkedPush.Token);
        }
        catch (OperationCanceledException) when (pushTimeout.IsCancellationRequested)
        {
            AppendTail(logTail, $"[push] timed out after {PushTimeout.TotalMinutes:0}m\n");
            return null;
        }
        catch (Exception ex)
        {
            AppendTail(logTail, $"[push] failed: {ex.Message}\n");
            logger.LogWarning(ex, "Registry push: push failed {Tag}", registryTag);
            return null;
        }

        if (pushError is not null)
        {
            // Daemon-reported error (e.g. "Get http://...: dial tcp:
            // lookup ... no such host", "denied: requested access to
            // the resource is denied"). Already in the log; bail so
            // the caller flips the build to Failed.
            logger.LogWarning("Registry push: daemon reported error: {Err}", pushError);
            return null;
        }

        AppendTail(logTail, $"[push] OK — image available at {registryTag}\n");
        return registryTag;
    }

    /// <summary>
    /// Best-effort post-build cleanup:
    /// <list type="number">
    ///   <item>Untag every <c>gzctf-auto/{gameId}/{slug}:*</c> local
    ///   tag whose digest suffix differs from <paramref name="keepDigest"/>.
    ///   The new build is the only useful one to keep — older content
    ///   SHAs from previous edits will never be re-referenced.</item>
    ///   <item>Prune dangling images (untagged + no children). Removes
    ///   the intermediate layers freed by step 1.</item>
    /// </list>
    /// <para>Build-cache prune is intentionally NOT done here — the
    /// Docker.DotNet API exposed in 3.131.1 has no
    /// <c>BuildPruneAsync</c>. Operators who need that can run
    /// <c>docker builder prune -af</c> on the host manually, or use
    /// the existing "Prune images" button on /admin/builds.</para>
    /// <para>Every step swallows its own errors and appends a line to
    /// the build log. A cleanup hiccup must not flip the build outcome
    /// from Success to Failed.</para>
    /// </summary>
    private async Task CleanupAfterBuildAsync(
        int gameId, string slug, string keepDigest,
        StringBuilder logTail, CancellationToken token)
    {
        var repository = $"gzctf-auto/{gameId}/{slug}";
        var keepFullTag = $"{repository}:{keepDigest}";

        // 1. Drop sibling tags.
        int removed = 0;
        try
        {
            var images = await _client.Images.ListImagesAsync(
                new ImagesListParameters { All = false }, token);
            foreach (var img in images)
            {
                if (img.RepoTags is null) continue;
                foreach (var rt in img.RepoTags)
                {
                    if (!rt.StartsWith(repository + ":", StringComparison.Ordinal)) continue;
                    if (string.Equals(rt, keepFullTag, StringComparison.Ordinal)) continue;
                    try
                    {
                        await _client.Images.DeleteImageAsync(rt,
                            new ImageDeleteParameters { Force = false, NoPrune = false }, token);
                        removed++;
                    }
                    catch (Exception ex)
                    {
                        AppendTail(logTail, $"[cleanup] failed to untag {rt}: {ex.Message}\n");
                    }
                }
            }
            if (removed > 0)
                AppendTail(logTail, $"[cleanup] removed {removed} older tag(s) for {repository}\n");
        }
        catch (Exception ex)
        {
            AppendTail(logTail, $"[cleanup] listing tags failed: {ex.Message}\n");
        }

        // 2. Prune dangling images. Removes anything untagged with no
        // children — typically the orphaned intermediate layers freed
        // by step 1.
        try
        {
            var p = await _client.Images.PruneImagesAsync(
                new ImagesPruneParameters { Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["dangling"] = new Dictionary<string, bool> { ["true"] = true }
                } }, token);
            if ((p?.SpaceReclaimed ?? 0) > 0)
                AppendTail(logTail, $"[cleanup] pruned dangling images: {HumanBytes(p.SpaceReclaimed)} reclaimed\n");
        }
        catch (Exception ex)
        {
            AppendTail(logTail, $"[cleanup] image prune failed: {ex.Message}\n");
        }
    }

    internal static string HumanBytes(ulong bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#}KB";
        if (bytes < 1024UL * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#}MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##}GB";
    }

    /// <summary>
    /// Reverse the XOR obfuscation applied at config-save time. If the
    /// XorKey is empty (test setups) or the stored value isn't valid
    /// base64, fall through to returning the raw stored value — that's
    /// the same defensive behavior the existing key-pair handling uses.
    /// </summary>
    private string DecryptPassword(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        if (_xorKey.Length == 0) return stored;
        try
        {
            return System.Text.Encoding.UTF8.GetString(
                Codec.Xor(Convert.FromBase64String(stored), _xorKey));
        }
        catch
        {
            // Pre-encryption legacy value or malformed input — try the
            // stored value as-is so a misconfigured XorKey doesn't
            // permanently break pushes.
            return stored;
        }
    }

    /// <summary>
    /// Token-shape scrubber for build output. A Dockerfile that
    /// <c>echo</c>s a GitHub PAT (legitimately during a `gh auth status`
    /// or buggily during a leaked $GITHUB_TOKEN) would otherwise land
    /// the raw token in <c>Challenge.LastBuildLog</c> and
    /// <c>ChallengeBuildAudit.LogTail</c>, both of which are visible
    /// to anyone with admin access. Replace before append.
    ///
    /// <para>The patterns are GitHub's well-known PAT prefixes plus
    /// the AWS access-key shape. False positives are acceptable — a
    /// scrubbed log is more useful than a leaked secret.</para>
    /// </summary>
    private static readonly Regex[] SecretPatterns =
    [
        new Regex(@"ghp_[A-Za-z0-9]{36}", RegexOptions.Compiled),
        new Regex(@"github_pat_[A-Za-z0-9_]{82,}", RegexOptions.Compiled),
        new Regex(@"gho_[A-Za-z0-9]{36}", RegexOptions.Compiled),
        new Regex(@"ghs_[A-Za-z0-9]{36}", RegexOptions.Compiled),
        new Regex(@"ghr_[A-Za-z0-9]{36}", RegexOptions.Compiled),
        new Regex(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled),
    ];

    internal static string ScrubSecrets(string line)
    {
        foreach (var p in SecretPatterns)
            line = p.Replace(line, "***SCRUBBED***");
        return line;
    }

    /// <summary>
    /// Gzip-tar the build context at <paramref name="contextDir"/> into
    /// <paramref name="outputTarGzPath"/> and return the lowercase hex SHA-256 of the
    /// NORMALIZED, uncompressed tar payload. The digest is a STABLE content hash:
    /// byte-identical challenge content always produces the same digest, regardless of
    /// filesystem enumeration order or file mtimes, so a re-import of unchanged content
    /// hits the cached image instead of minting a new <c>gzctf-auto</c> tag.
    ///
    /// <para>Determinism is achieved by (1) sorting entries by ordinal relative path,
    /// (2) pinning each entry's mtime + mode to fixed values (a fresh PaxTarEntry
    /// otherwise stamps <c>DateTimeOffset.UtcNow</c>), and (3) hashing the tar bytes
    /// rather than the gzip output so the gzip header/level can't perturb the digest.</para>
    /// </summary>
    internal static async Task<string> WriteContextTarAsync(
        string contextDir, string outputTarGzPath, CancellationToken token)
    {
        using var sha = SHA256.Create();
        await using (var fs = File.Create(outputTarGzPath))
        await using (var gz = new GZipStream(fs, CompressionLevel.Fastest, leaveOpen: false))
        await using (var hashing = new CryptoStream(gz, sha, CryptoStreamMode.Write, leaveOpen: false))
        await using (var tar = new TarWriter(hashing, leaveOpen: false))
        {
            var entries = Directory.EnumerateFiles(contextDir, "*", SearchOption.AllDirectories)
                .Select(full => (full, rel: Path.GetRelativePath(contextDir, full).Replace('\\', '/')))
                .Where(x => !x.rel.StartsWith("..", StringComparison.Ordinal))
                .OrderBy(x => x.rel, StringComparer.Ordinal);
            foreach (var (full, rel) in entries)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, rel)
                {
                    DataStream = File.OpenRead(full),
                    ModificationTime = BuildEntryMTime,
                    Mode = BuildEntryMode,
                };
                await tar.WriteEntryAsync(entry, token);
                entry.DataStream?.Dispose();
            }
        }
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    static void AppendTail(StringBuilder sb, string line)
    {
        line = ScrubSecrets(line);
        sb.Append(line);
        if (sb.Length > LogTailBytes)
            sb.Remove(0, sb.Length - LogTailBytes);
    }

    internal static string NormalizeSlug(string s)
    {
        var clean = new StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
            clean.Append(char.IsLetterOrDigit(c) ? c : '-');
        var slug = clean.ToString().Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Length > 0 ? slug : "challenge";
    }

    /// <summary>
    /// Test-only helper: re-uses the same XOR + base64 path as the
    /// instance-bound <see cref="DecryptPassword"/> so unit tests can
    /// verify the round-trip without standing up a full instance.
    /// </summary>
    internal static string DecryptXorPassword(string? stored, byte[] xorKey)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        if (xorKey.Length == 0) return stored;
        try
        {
            return System.Text.Encoding.UTF8.GetString(
                Codec.Xor(Convert.FromBase64String(stored), xorKey));
        }
        catch
        {
            return stored;
        }
    }
}
