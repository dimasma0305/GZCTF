using GZCTF.Utils;

namespace GZCTF.Services;

/// <summary>
/// One-shot startup security audit — the "fail loud" half of the preflight.
/// Inspects config an operator can get wrong (deployment-specific, security-
/// sensitive) and logs prominent warnings; it never silently overrides an
/// operator's decision. The unambiguous fixes are auto-enforced elsewhere
/// (e.g. the private/link-local egress baseline + auto node-network deny in
/// <see cref="Container.Provider.KubernetesProvider"/>).
///
/// <para>Warnings go through <c>SystemLog</c>, so they surface in the admin log
/// view as well as the console.</para>
/// </summary>
public sealed class StartupSecurityAudit(
    IConfiguration configuration,
    ILogger<StartupSecurityAudit> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var findings = new List<string>();

        // 1. X-Forwarded-For trusted from everywhere → client IP is spoofable,
        //    defeating IP bans, rate-limit, and the cross-team-IP cheat
        //    correlation. The correct value is the operator's proxy topology,
        //    which we can't guess — so warn rather than rewrite.
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
        //    reach challenge pods (pods use external DNS, can't resolve cluster
        //    names) — the game silently never plants flags.
        if (string.Equals(configuration["ContainerProvider:Type"], "Kubernetes", StringComparison.OrdinalIgnoreCase))
        {
            var baseUrl = configuration["Ad:FlagPullBaseUrl"];
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) || !System.Net.IPAddress.TryParse(u.Host, out _))
                findings.Add(
                    "Ad:FlagPullBaseUrl is unset or not an IP — A&D flags can't be delivered to challenge pods " +
                    "on Kubernetes. Set it to an IP:port the pods can reach.");
        }

        if (findings.Count == 0)
        {
            logger.SystemLog("Startup security audit: no issues detected.", TaskStatus.Success, LogLevel.Information);
            return Task.CompletedTask;
        }

        logger.SystemLog($"Startup security audit found {findings.Count} issue(s):",
            TaskStatus.Failed, LogLevel.Warning);
        foreach (var f in findings)
            logger.SystemLog($"  ⚠ {f}", TaskStatus.Failed, LogLevel.Warning);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
