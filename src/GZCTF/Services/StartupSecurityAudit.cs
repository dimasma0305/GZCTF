using GZCTF.Services.Container.Provider;
using GZCTF.Utils;
using k8s;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;

namespace GZCTF.Services;

/// <summary>
/// Startup security preflight — the "fail loud" half of the defense-in-depth
/// posture (the unambiguous fixes are auto-enforced in
/// <see cref="KubernetesProvider"/>). Two parts, both best-effort and
/// non-blocking (BackgroundService): a synchronous config audit, then — on
/// Kubernetes — an active NetworkPolicy-enforcement self-test.
///
/// <para>All warnings go through <c>SystemLog</c>, so they surface in the admin
/// log view as well as the console. The audit never overrides an operator's
/// decision and never fails startup.</para>
/// </summary>
public sealed class StartupSecurityAudit(
    IConfiguration configuration,
    IServiceProvider services,
    ILogger<StartupSecurityAudit> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RunConfigAudit();
        try { await VerifyNetworkPolicyEnforcementAsync(stoppingToken); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "NetworkPolicy enforcement self-test errored (inconclusive)");
        }
    }

    /// <summary>Static config checks for deployment-specific, security-sensitive
    /// values we can't safely auto-fix.</summary>
    private void RunConfigAudit()
    {
        var findings = new List<string>();

        // 1. X-Forwarded-For trusted from everywhere → spoofable client IPs
        //    (defeats IP bans, rate-limit, cross-team-IP cheat detection).
        var trusted = configuration.GetSection("ForwardedOptions:TrustedNetworks").Get<string[]>() ?? [];
        if (trusted.Any(n => n is "0.0.0.0/0" or "::/0"))
            findings.Add(
                "ForwardedOptions.TrustedNetworks trusts 0.0.0.0/0 — X-Forwarded-For is honoured from ANY " +
                "source, so client IPs are spoofable (IP bans, rate-limit, and cross-team-IP cheat detection " +
                "can be forged). Set it to your ingress/proxy source only.");

        // 2. Rate limiting disabled → login/submission brute-force exposure.
        if (configuration.GetValue<bool>("DisableRateLimit"))
            findings.Add(
                "DisableRateLimit is true — login/submission rate limiting is OFF (brute-force exposure). " +
                "Set it to false in production.");

        // 3. A&D on K8s needs FlagPullBaseUrl (an IP) or rotating flags never
        //    reach challenge pods — the game silently never plants flags.
        if (string.Equals(configuration["ContainerProvider:Type"], "Kubernetes", StringComparison.OrdinalIgnoreCase))
        {
            var baseUrl = configuration["Ad:FlagPullBaseUrl"];
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) || !System.Net.IPAddress.TryParse(u.Host, out _))
                findings.Add(
                    "Ad:FlagPullBaseUrl is unset or not an IP — A&D flags can't be delivered to challenge pods " +
                    "on Kubernetes. Set it to an IP:port the pods can reach.");

            // 3b. On Kubernetes, only the Open/Isolated NetworkModes get a built-in
            //     egress NetworkPolicy. A challenge set to NetworkMode.Custom gets
            //     ONLY a pod label (an operator extension point) and NO egress policy,
            //     so it is uncontained by default — it can reach cloud metadata,
            //     the cluster control plane, and other teams' pods. If you use Custom
            //     mode, supply your own NetworkPolicy (selector
            //     gzctf.gzti.me/NetworkMode=custom) with the egress restrictions you want.
            findings.Add(
                "Kubernetes provider: NetworkMode.Custom challenges get NO built-in egress NetworkPolicy " +
                "(only a pod label). Such a challenge is uncontained (can reach cloud metadata / the control " +
                "plane / other pods) unless YOU add a NetworkPolicy for selector gzctf.gzti.me/NetworkMode=custom. " +
                "Avoid Custom mode for untrusted challenges, or pin a policy.");
        }

        // 3c. Ad:Ssh:InternalSecret gates the internal SSH lookup/relay endpoints
        //     (InternalAdSshController already fails closed at request time if this
        //     is unset or the shipped placeholder — see AuthInternalValue — but that
        //     only surfaces as a per-request LogError once someone tries to use it).
        //     Surface it here too so an operator sees "SSH jump-host is disabled"
        //     immediately at boot, not after a confused support ticket.
        var sshSecret = configuration["Ad:Ssh:InternalSecret"];
        if (string.IsNullOrEmpty(sshSecret))
            findings.Add(
                "Ad:Ssh:InternalSecret is unset — the internal A&D SSH lookup/relay endpoints are DISABLED " +
                "(fail closed). Set AD_SSH_INTERNAL_SECRET to a real secret if you use the SSH jump-host feature.");
        else if (sshSecret == "dev-only-rotate-me-before-prod")
            findings.Add(
                "Ad:Ssh:InternalSecret is still the shipped placeholder — the internal A&D SSH lookup/relay " +
                "endpoints are DISABLED (fail closed). Set AD_SSH_INTERNAL_SECRET to a real secret.");

        // 4. AllowedHosts unset / "*" → Host-header injection. Email links (password
        //    reset / verification) and the BYOC setup script use the request Host, so
        //    a spoofed Host header can phish users or mis-point a team's agent. Behind
        //    a Host-routing reverse proxy (which only forwards your domain) this is
        //    already moot; a DIRECT-facing server should pin AllowedHosts.
        var allowedHosts = configuration["AllowedHosts"];
        if (string.IsNullOrWhiteSpace(allowedHosts)
            || allowedHosts.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(h => h == "*"))
            findings.Add(
                "AllowedHosts is unset or \"*\" — email links and the BYOC setup script trust the request Host, so " +
                "a spoofed Host header can phish users / mis-point team agents. If this server is directly " +
                "internet-facing (not behind a Host-routing reverse proxy), set AllowedHosts to your public " +
                "host(s), e.g. AllowedHosts=ctf.example.com.");

        if (findings.Count == 0)
        {
            logger.SystemLog("Startup security audit: no config issues detected.", TaskStatus.Success, LogLevel.Information);
            return;
        }

        logger.SystemLog($"Startup security audit found {findings.Count} config issue(s):",
            TaskStatus.Failed, LogLevel.Warning);
        foreach (var f in findings)
            logger.SystemLog($"  ⚠ {f}", TaskStatus.Failed, LogLevel.Warning);
    }

    /// <summary>
    /// Prove the CNI actually enforces NetworkPolicy: spawn a throwaway pod
    /// labelled <c>NetworkMode=isolated</c> (so the isolated egress policy applies
    /// — deny-all except flag-pull + DNS) and have it try to TCP-connect the
    /// kube-api ClusterIP. If the connect SUCCEEDS the policy isn't being enforced
    /// and every A&amp;D isolation guarantee is silently void. Reading pod logs (not
    /// exec) keeps it simple; bounded and cleaned up; never fatal.
    /// </summary>
    private async Task VerifyNetworkPolicyEnforcementAsync(CancellationToken token)
    {
        if (!string.Equals(configuration["ContainerProvider:Type"], "Kubernetes", StringComparison.OrdinalIgnoreCase))
            return;
        if (!configuration.GetValue("ContainerProvider:KubernetesConfig:VerifyNetworkPolicy", true))
            return;

        var provider = services.GetService<IContainerProvider<Kubernetes, KubernetesMetadata>>();
        if (provider is null)
            return;

        var client = provider.GetProvider();
        var ns = provider.GetMetadata().Config.Namespace;
        var image = configuration["ContainerProvider:KubernetesConfig:NetworkProbeImage"] ?? "busybox:stable";
        var podName = $"gzctf-netpol-probe-{Guid.NewGuid():N}"[..36];

        // kube-api ClusterIP is always-listening + always-denied (it sits in the
        // egress baseline, and is neither the flag-pull host nor DNS). Discover it
        // for portability; fall back to the k3s default if RBAC forbids the read.
        var apiIp = "10.43.0.1";
        try
        {
            var svc = await client.CoreV1.ReadNamespacedServiceAsync("kubernetes", "default", cancellationToken: token);
            if (!string.IsNullOrEmpty(svc.Spec?.ClusterIP) && svc.Spec.ClusterIP != "None")
                apiIp = svc.Spec.ClusterIP;
        }
        catch { /* RBAC / not found — keep the default */ }

        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = podName,
                Labels = new Dictionary<string, string> { ["gzctf.gzti.me/NetworkMode"] = "isolated" }
            },
            Spec = new V1PodSpec
            {
                RestartPolicy = "Never",
                AutomountServiceAccountToken = false,
                ActiveDeadlineSeconds = 45,
                Containers =
                [
                    new V1Container
                    {
                        Name = "probe",
                        Image = image,
                        ImagePullPolicy = "IfNotPresent",
                        // Warm-up sleep first: kube-router (and other CNIs) program a new
                        // pod's egress rules ~1-2s AFTER it starts, so probing immediately
                        // races that window and false-positives "reachable". After warm-up,
                        // retry a few times — a single successful connect ⇒ NOT enforced;
                        // all attempts blocked ⇒ enforced.
                        Command =
                        [
                            "sh", "-c",
                            $"sleep 8; for i in 1 2 3; do nc -w 3 {apiIp} 443 </dev/null >/dev/null 2>&1 && " +
                            "{ echo NETPOL_PROBE_EXIT:0; exit 0; }; sleep 2; done; echo NETPOL_PROBE_EXIT:blocked"
                        ]
                    }
                ]
            }
        };

        try
        {
            await client.CoreV1.CreateNamespacedPodAsync(pod, ns, cancellationToken: token);

            // Wait (bounded) for the one-shot to finish.
            string? phase = null;
            for (var i = 0; i < 30 && phase is not ("Succeeded" or "Failed"); i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
                try { phase = (await client.CoreV1.ReadNamespacedPodAsync(podName, ns, cancellationToken: token)).Status?.Phase; }
                catch { /* transient */ }
            }

            if (phase is not ("Succeeded" or "Failed"))
            {
                logger.SystemLog(
                    $"NetworkPolicy self-test inconclusive: probe pod didn't run (phase={phase ?? "pending"}; image pullable?).",
                    TaskStatus.Failed, LogLevel.Warning);
                return;
            }

            string logs;
            await using (var stream = await client.CoreV1.ReadNamespacedPodLogAsync(podName, ns, cancellationToken: token))
            using (var reader = new StreamReader(stream))
                logs = await reader.ReadToEndAsync(token);

            if (logs.Contains("NETPOL_PROBE_EXIT:0"))
                logger.SystemLog(
                    "⚠ NetworkPolicy is NOT enforced by your CNI — an isolated challenge reached the kube-api " +
                    $"ClusterIP ({apiIp}:443). A&D network isolation (team↔team, control-plane, metadata) is VOID. " +
                    "Use a NetworkPolicy-enforcing CNI (Calico/Cilium, or k3s with its built-in controller).",
                    TaskStatus.Failed, LogLevel.Error);
            else if (logs.Contains("NETPOL_PROBE_EXIT:"))
                logger.SystemLog("NetworkPolicy enforcement confirmed (isolated egress is blocked).",
                    TaskStatus.Success, LogLevel.Information);
            else
                logger.SystemLog("NetworkPolicy self-test inconclusive (no probe output).",
                    TaskStatus.Failed, LogLevel.Warning);
        }
        finally
        {
            try
            {
                await client.CoreV1.DeleteNamespacedPodAsync(podName, ns,
                    new V1DeleteOptions { GracePeriodSeconds = 0 }, cancellationToken: CancellationToken.None);
            }
            catch { /* already gone */ }
        }
    }
}
