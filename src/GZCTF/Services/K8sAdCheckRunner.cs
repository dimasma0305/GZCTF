using GZCTF.Models.Data;
using GZCTF.Services.Container.Provider;
using GZCTF.Utils;
using k8s;
using k8s.Models;

namespace GZCTF.Services;

/// <summary>
/// Kubernetes <see cref="IAdCheckRunner"/>: runs one A&amp;D check as a short-lived
/// Pod (virtual nodes / k3s can't <c>exec</c> reliably, so we don't — we read
/// the verdict from <b>pod status + logs</b>). Creates a pod with the
/// enochecker3 env contract pointed at the team service's ClusterIP, waits for
/// it to terminate, maps the container exit code via
/// <see cref="AdCheckMapping"/>, captures logs for the message, then deletes it.
///
/// <para>Registered only under the Kubernetes provider; Docker uses
/// <see cref="AdCheckerExecutor"/>.</para>
/// </summary>
public sealed class K8sAdCheckRunner(
    IContainerProvider<Kubernetes, KubernetesMetadata> provider,
    IConfiguration configuration,
    ILogger<K8sAdCheckRunner> logger) : IAdCheckRunner
{
    private const string FallbackImage = "alpine:3.21";
    private const int MaxErrorMessageLength = 4096;

    private TimeSpan Timeout => TimeSpan.FromSeconds(
        int.TryParse(configuration["Ad:Checker:TimeoutSeconds"], out var s) && s is > 0 and <= 600 ? s : 30);

    public async Task<AdCheckOutcome> RunAsync(
        AdTeamService ts, AdRound round, GameChallenge challenge, string? plantedFlag, CancellationToken token)
    {
        if (ts.Container is null || string.IsNullOrEmpty(ts.Container.IP))
            return new AdCheckOutcome(AdCheckStatus.Offline, "target container has no IP", null);

        var client = provider.GetProvider();
        var ns = provider.GetMetadata().Config.Namespace;
        var targetIp = ts.Container.IP;
        var targetPort = challenge.ExposePort ?? 80;
        var useCustomChecker = !string.IsNullOrWhiteSpace(challenge.AdCheckerImage);
        var image = useCustomChecker ? challenge.AdCheckerImage!.Trim() : FallbackImage;

        var name = $"ad-checker-{ts.ParticipationId}-{challenge.Id}-{round.Number}-{Guid.NewGuid().ToString("N")[..8]}"
            .ToValidRFC1123String("ad-checker");

        var env = new List<V1EnvVar>
        {
            new() { Name = "GZCTF_ACTION", Value = "check" },
            new() { Name = "GZCTF_TARGET_IP", Value = targetIp },
            new() { Name = "GZCTF_TARGET_PORT", Value = targetPort.ToString() },
            new() { Name = "GZCTF_ROUND", Value = round.Number.ToString() },
            new() { Name = "GZCTF_TEAM_ID", Value = ts.ParticipationId.ToString() },
            new() { Name = "GZCTF_CHALLENGE_ID", Value = challenge.Id.ToString() }
        };
        if (!string.IsNullOrEmpty(plantedFlag))
            env.Add(new V1EnvVar { Name = "GZCTF_FLAG", Value = plantedFlag });

        var checker = new V1Container
        {
            Name = "checker",
            Image = image,
            ImagePullPolicy = provider.GetMetadata().Config.ImagePullPolicy,
            Env = env,
            // Built-in TCP probe when no custom checker image is set.
            Command = useCustomChecker ? null : ["sh", "-c", $"nc -z -w3 {targetIp} {targetPort}"],
            Resources = new V1ResourceRequirements
            {
                Limits = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("500m"), ["memory"] = new("256Mi") },
                Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("10m"), ["memory"] = new("32Mi") }
            }
        };

        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = ns,
                Labels = new Dictionary<string, string>
                {
                    ["gzctf.gzti.me/ResourceId"] = name,
                    ["gzctf.role"] = "ad-checker",
                    // Open egress so the checker can reach the target's ClusterIP.
                    ["gzctf.gzti.me/NetworkMode"] = "open"
                }
            },
            Spec = new V1PodSpec
            {
                Containers = [checker],
                RestartPolicy = "Never",
                AutomountServiceAccountToken = false,
                DnsPolicy = "None",
                DnsConfig = new() { Nameservers = ["223.5.5.5", "114.114.114.114"] }
            }
        };

        try
        {
            await client.CreateNamespacedPodAsync(pod, ns, cancellationToken: token);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "K8sAdChecker: create pod failed for service={Sid} round={Round}", ts.Id, round.Number);
            return new AdCheckOutcome(AdCheckStatus.InternalError, Trunc($"checker pod create error: {e.Message}"), null);
        }

        string? sourceIp = null;
        try
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            waitCts.CancelAfter(Timeout);

            V1ContainerStateTerminated? term = null;
            while (!waitCts.IsCancellationRequested)
            {
                var p = await client.ReadNamespacedPodAsync(name, ns, cancellationToken: waitCts.Token);
                sourceIp = p.Status?.PodIP ?? sourceIp;
                if (p.Status?.Phase is "Succeeded" or "Failed")
                {
                    term = p.Status.ContainerStatuses?.FirstOrDefault()?.State?.Terminated;
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), waitCts.Token);
            }

            if (term is null)
                return new AdCheckOutcome(AdCheckStatus.Offline, $"checker timeout after {Timeout.TotalSeconds:0}s", sourceIp);

            var status = AdCheckMapping.FromExitCode(term.ExitCode, useCustomChecker);
            var logs = await TryFetchLogsAsync(client, name, ns, token);
            var message = status == AdCheckStatus.Ok
                ? null
                : Trunc(string.IsNullOrWhiteSpace(logs)
                    ? $"checker exit {term.ExitCode} ({status})"
                    : $"exit {term.ExitCode} ({status}): {logs.Trim()}");
            return new AdCheckOutcome(status, message, sourceIp);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new AdCheckOutcome(AdCheckStatus.Offline, $"checker timeout after {Timeout.TotalSeconds:0}s", sourceIp);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "K8sAdChecker: run failed for service={Sid} round={Round}", ts.Id, round.Number);
            return new AdCheckOutcome(AdCheckStatus.InternalError, Trunc($"checker run error: {e.Message}"), sourceIp);
        }
        finally
        {
            try { await client.CoreV1.DeleteNamespacedPodAsync(name, ns, cancellationToken: CancellationToken.None); }
            catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Built-in reachability probe for a whole tick in ONE Pod (gzctf can't reach
    /// pod ClusterIPs from outside the cluster, so the probe must run in-cluster).
    /// The pod TCP-connects to every target in parallel and prints
    /// <c>"&lt;serviceId&gt; ok|down"</c> per line; we read the log and map it.
    /// One pod per tick instead of one per check.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, AdCheckStatus>> RunBuiltinBatchAsync(
        IReadOnlyList<AdBuiltinTarget> targets, CancellationToken token)
    {
        // Default Offline; flipped to Ok only for targets the prober reports up.
        var results = new Dictionary<int, AdCheckStatus>(targets.Count);
        foreach (var t in targets)
            results[t.ServiceId] = AdCheckStatus.Offline;
        if (targets.Count == 0)
            return results;

        var client = provider.GetProvider();
        var ns = provider.GetMetadata().Config.Namespace;
        var name = $"ad-prober-{Guid.NewGuid().ToString("N")[..12]}".ToValidRFC1123String("ad-prober");

        // "ip|port|serviceId" entries; probed concurrently (& … wait) so the whole
        // batch finishes in ~one nc timeout regardless of team count.
        var list = string.Join(' ', targets.Select(t => $"{t.Ip}|{t.Port}|{t.ServiceId}"));
        const string script =
            "for e in $TARGETS; do ( ip=${e%%|*}; r=${e#*|}; p=${r%%|*}; s=${r##*|}; " +
            "if nc -w3 \"$ip\" \"$p\" </dev/null >/dev/null 2>&1; then echo \"$s ok\"; else echo \"$s down\"; fi ) & done; wait";

        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = ns,
                Labels = new Dictionary<string, string>
                {
                    ["gzctf.gzti.me/ResourceId"] = name,
                    ["gzctf.role"] = "ad-checker",
                    ["gzctf.gzti.me/NetworkMode"] = "open"
                }
            },
            Spec = new V1PodSpec
            {
                Containers =
                [
                    new V1Container
                    {
                        Name = "prober",
                        Image = FallbackImage,
                        ImagePullPolicy = provider.GetMetadata().Config.ImagePullPolicy,
                        Env = [new V1EnvVar { Name = "TARGETS", Value = list }],
                        Command = ["sh", "-c", script],
                        Resources = new V1ResourceRequirements
                        {
                            Limits = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("500m"), ["memory"] = new("128Mi") },
                            Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("10m"), ["memory"] = new("32Mi") }
                        }
                    }
                ],
                RestartPolicy = "Never",
                AutomountServiceAccountToken = false,
                DnsPolicy = "None",
                DnsConfig = new() { Nameservers = ["223.5.5.5", "114.114.114.114"] }
            }
        };

        try
        {
            await client.CreateNamespacedPodAsync(pod, ns, cancellationToken: token);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "K8sAdChecker: prober pod create failed ({N} targets)", targets.Count);
            return results;
        }

        try
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            waitCts.CancelAfter(Timeout);

            var done = false;
            while (!waitCts.IsCancellationRequested)
            {
                var p = await client.ReadNamespacedPodAsync(name, ns, cancellationToken: waitCts.Token);
                if (p.Status?.Phase is "Succeeded" or "Failed") { done = true; break; }
                await Task.Delay(TimeSpan.FromSeconds(1), waitCts.Token);
            }

            if (!done)
                return results; // timeout → leave Offline

            var logs = await TryFetchLogsAsync(client, name, ns, token);
            if (!string.IsNullOrEmpty(logs))
            {
                foreach (var line in logs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && parts[1] == "ok" && int.TryParse(parts[0], out var sid))
                        results[sid] = AdCheckStatus.Ok;
                }
            }

            return results;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return results;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "K8sAdChecker: prober run failed ({N} targets)", targets.Count);
            return results;
        }
        finally
        {
            try { await client.CoreV1.DeleteNamespacedPodAsync(name, ns, cancellationToken: CancellationToken.None); }
            catch { /* best-effort cleanup */ }
        }
    }

    private static string Trunc(string s) => s.Length > MaxErrorMessageLength ? s[..MaxErrorMessageLength] : s;

    private static async Task<string?> TryFetchLogsAsync(Kubernetes client, string name, string ns, CancellationToken token)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            await using var stream = await client.CoreV1.ReadNamespacedPodLogAsync(name, ns, cancellationToken: cts.Token);
            using var reader = new StreamReader(stream);
            var s = (await reader.ReadToEndAsync(cts.Token)).Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        catch
        {
            return null;
        }
    }
}
