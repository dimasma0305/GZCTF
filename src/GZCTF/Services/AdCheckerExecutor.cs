using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using GZCTF.Models.Data;
using GZCTF.Services.Container.Provider;
using GZCTF.Utils;

namespace GZCTF.Services;

/// <summary>
/// Docker <see cref="IAdCheckRunner"/>: runs a single A&amp;D check by spawning a
/// checker container on the target's network and returns an
/// <see cref="AdCheckOutcome"/> the scheduler persists as an
/// <see cref="AdCheckResult"/> row. (K8s uses <see cref="K8sAdCheckRunner"/>.)
///
/// <para>Two modes selected by <see cref="GameChallenge.AdCheckerImage"/>:</para>
/// <list type="number">
///   <item><b>Operator-supplied checker image</b> (non-empty
///         <see cref="GameChallenge.AdCheckerImage"/>): launches the image
///         on the same docker network as the target with the enochecker3-style
///         env contract (<c>GZCTF_ACTION</c> / <c>GZCTF_TARGET_IP</c> /
///         <c>GZCTF_TARGET_PORT</c> / <c>GZCTF_FLAG</c> / <c>GZCTF_ROUND</c> /
///         <c>GZCTF_TEAM_ID</c>). Maps exit code 0/1/2/* to Ok/Mumble/Offline/InternalError.</item>
///   <item><b>Built-in TCP-reachability fallback</b> (no checker image set):
///         runs <c>alpine:3.21</c> with <c>nc -z -w3</c> against the
///         target's exposed port. Exit 0 → Ok, non-zero → Offline. No Mumble
///         distinction is possible without inspecting payloads.</item>
/// </list>
///
/// <para>Resource bounds (256 MiB / 0.5 CPU) + a configurable timeout cap
/// any runaway checker — a misbehaving image can't starve the rest of the
/// platform. <see cref="HostConfig.AutoRemove"/> is intentionally false so
/// we can read logs + inspect IP before removing.</para>
///
/// <para>Registered as <see cref="IAdCheckRunner"/> only under the Docker
/// provider; on Kubernetes <see cref="K8sAdCheckRunner"/> is registered instead.</para>
/// </summary>
public sealed class AdCheckerExecutor(
    IContainerProvider<DockerClient, DockerMetadata> provider,
    IConfiguration configuration,
    ILogger<AdCheckerExecutor> logger) : IAdCheckRunner
{
    private const string FallbackImage = "alpine:3.21";
    private const int MaxErrorMessageLength = 4096;

    private TimeSpan Timeout => TimeSpan.FromSeconds(
        int.TryParse(configuration["Ad:Checker:TimeoutSeconds"], out var s) && s is > 0 and <= 600 ? s : 30);

    public async Task<AdCheckOutcome> RunAsync(
        AdTeamService ts,
        AdRound round,
        GameChallenge challenge,
        string? plantedFlag,
        CancellationToken token)
    {
        if (ts.Container is null || string.IsNullOrEmpty(ts.Container.IP))
            return new AdCheckOutcome(AdCheckStatus.Offline, "target container has no IP", null);

        var targetIp = ts.Container.IP;
        var targetPort = challenge.ExposePort ?? 80;
        var meta = provider.GetMetadata();
        var networkMode = challenge.AdAllowEgress ? NetworkMode.Open : NetworkMode.Isolated;
        if (!meta.NetworkNames.TryGetValue(networkMode, out var networkName))
            return new AdCheckOutcome(AdCheckStatus.InternalError, $"no network for mode {networkMode}", null);

        var useCustomChecker = !string.IsNullOrWhiteSpace(challenge.AdCheckerImage);
        var image = useCustomChecker ? challenge.AdCheckerImage!.Trim() : FallbackImage;
        var cmd = useCustomChecker
            ? null
            : new[] { "sh", "-c", $"nc -z -w3 {targetIp} {targetPort}" };

        var env = new List<string>
        {
            $"GZCTF_ACTION=check",
            $"GZCTF_TARGET_IP={targetIp}",
            $"GZCTF_TARGET_PORT={targetPort}",
            $"GZCTF_ROUND={round.Number}",
            $"GZCTF_TEAM_ID={ts.ParticipationId}",
            $"GZCTF_CHALLENGE_ID={challenge.Id}"
        };
        if (!string.IsNullOrEmpty(plantedFlag))
            env.Add($"GZCTF_FLAG={plantedFlag}");

        var parameters = new CreateContainerParameters
        {
            Image = image,
            Cmd = cmd,
            Env = env,
            Labels = new Dictionary<string, string>
            {
                ["gzctf.role"] = "ad-checker",
                ["gzctf.challenge"] = challenge.Id.ToString(),
                ["gzctf.participation"] = ts.ParticipationId.ToString(),
                ["gzctf.round"] = round.Number.ToString()
            },
            HostConfig = new HostConfig
            {
                AutoRemove = false,
                NetworkMode = networkName,
                Memory = 256L * 1024 * 1024,
                NanoCPUs = 500_000_000L
            }
        };

        var client = provider.GetProvider();
        string? containerId = null;

        try
        {
            CreateContainerResponse created;
            try
            {
                created = await client.Containers.CreateContainerAsync(parameters, token);
            }
            catch (DockerImageNotFoundException)
            {
                // Pull the image lazily. First run after an operator sets a
                // new AdCheckerImage pays this cost — typically <30s. Any
                // checks in flight during the pull will land as InternalError
                // and recover on the next tick.
                logger.SystemLog($"AdChecker: pulling image {image}", TaskStatus.Pending, LogLevel.Information);
                await client.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = image }, null,
                    new Progress<JSONMessage>(_ => { }), token);
                created = await client.Containers.CreateContainerAsync(parameters, token);
            }

            containerId = created.ID;

            var started = await client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), token);
            if (!started)
                return new AdCheckOutcome(AdCheckStatus.InternalError, "container failed to start", null);

            // SourceIp: read it BEFORE waiting — the container may be removed
            // after exit by some setups, and we want a recorded value even
            // when the checker exits instantly.
            string? sourceIp = null;
            try
            {
                var info = await client.Containers.InspectContainerAsync(containerId, token);
                // Prefer the network we explicitly attached to; fall back to
                // whatever's first (Docker may have a slightly different key
                // — e.g. compose adds a prefix on bridge names).
                var nets = info.NetworkSettings?.Networks;
                if (nets is not null)
                {
                    sourceIp = nets.TryGetValue(networkName, out var endpoint)
                        ? endpoint.IPAddress
                        : nets.Values.FirstOrDefault()?.IPAddress;
                }
            }
            catch
            {
                // best-effort; SourceIp stays null
            }

            // Wait with a hard timeout. A misbehaving checker that never exits
            // would otherwise consume a slot on the scheduler's semaphore
            // forever.
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            waitCts.CancelAfter(Timeout);

            ContainerWaitResponse? waitResp = null;
            try
            {
                waitResp = await client.Containers.WaitContainerAsync(containerId, waitCts.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // Timed out — force-kill so RemoveContainerAsync in finally
                // doesn't have to wait for the orphan.
                try { await client.Containers.KillContainerAsync(containerId, new ContainerKillParameters(), CancellationToken.None); }
                catch { /* container may already be gone */ }
                return new AdCheckOutcome(AdCheckStatus.Offline,
                    $"checker timeout after {Timeout.TotalSeconds:0}s", sourceIp);
            }

            var exitCode = (int)(waitResp?.StatusCode ?? -1);
            var stderr = await TryFetchLogsAsync(client, containerId, token);

            var status = AdCheckMapping.FromExitCode(exitCode, useCustomChecker);
            var message = BuildMessage(status, exitCode, stderr, useCustomChecker);
            return new AdCheckOutcome(status, message, sourceIp);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "AdChecker: run failed for service={Sid} round={Round}", ts.Id, round.Number);
            return new AdCheckOutcome(AdCheckStatus.InternalError, Trunc($"checker run error: {e.Message}"), null);
        }
        finally
        {
            if (containerId is not null)
            {
                try
                {
                    await client.Containers.RemoveContainerAsync(containerId,
                        new ContainerRemoveParameters { Force = true }, CancellationToken.None);
                }
                catch (Exception e)
                {
                    logger.LogDebug(e, "AdChecker: cleanup failed for {Cid}", containerId);
                }
            }
        }
    }

    /// <summary>
    /// Built-in reachability probe, in-process: gzctf is attached to the
    /// challenge bridges, so it can TCP-connect to each target directly — no
    /// per-check <c>alpine nc</c> container. Connect OK → Ok, else Offline.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, AdCheckStatus>> RunBuiltinBatchAsync(
        IReadOnlyList<AdBuiltinTarget> targets, CancellationToken token)
    {
        var results = new ConcurrentDictionary<int, AdCheckStatus>();
        var probeTimeout = TimeSpan.FromSeconds(5);
        var opts = new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = token };
        await Parallel.ForEachAsync(targets, opts, async (t, ct) =>
        {
            results[t.ServiceId] = await TcpReachableAsync(t.Ip, t.Port, probeTimeout, ct)
                ? AdCheckStatus.Ok
                : AdCheckStatus.Offline;
        });
        return results;
    }

    private static async Task<bool> TcpReachableAsync(string ip, int port, TimeSpan timeout, CancellationToken token)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeout);
            await client.ConnectAsync(ip, port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static string? BuildMessage(AdCheckStatus status, int exitCode, string? stderr, bool useCustomChecker)
    {
        if (status == AdCheckStatus.Ok) return null;
        if (!useCustomChecker)
            return Trunc($"tcp probe failed (exit {exitCode})");

        return string.IsNullOrWhiteSpace(stderr)
            ? Trunc($"checker exit {exitCode} ({status})")
            : Trunc($"exit {exitCode} ({status}): {stderr.Trim()}");
    }

    private static string Trunc(string s) =>
        s.Length > MaxErrorMessageLength ? s[..MaxErrorMessageLength] : s;

    private static async Task<string?> TryFetchLogsAsync(DockerClient client, string containerId, CancellationToken token)
    {
        try
        {
            var buf = new StringBuilder(1024);
            var progress = new Progress<string>(line =>
            {
                if (buf.Length > 4096) return;
                buf.Append(line);
                if (!line.EndsWith('\n')) buf.Append('\n');
            });
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            await client.Containers.GetContainerLogsAsync(containerId,
                new ContainerLogsParameters
                {
                    ShowStdout = true,
                    ShowStderr = true,
                    Tail = "200"
                }, progress, cts.Token);
            var s = buf.ToString().Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        catch
        {
            return null;
        }
    }
}
