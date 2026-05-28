using System.Net;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using GZCTF.Models;
using GZCTF.Services.Container.Provider;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GZCTF.Services;

/// <summary>
/// Brings the Docker provider to A&amp;D egress-isolation parity with Kubernetes: a
/// fully-popped challenge container must not be able to pivot to other teams'
/// containers or reach cloud metadata / private ranges, while the intended paths
/// (the checker, VPN ingress via the WireGuard sidecar, and internet egress on
/// "open") keep working.
///
/// <para>K8s enforces this with a per-CIDR egress NetworkPolicy. Docker has no such
/// API, so we install equivalent rules in Docker's <c>DOCKER-USER</c> chain (host
/// has <c>bridge-nf-call-iptables=1</c>, so even bridged same-subnet traffic
/// traverses it). The block is keyed on an <b>ipset of the actual challenge-container
/// IPs</b> (from the DB) rather than the whole bridge subnet — so gzctf, the WG
/// sidecar, and the ephemeral checker containers (none of which are in the set) are
/// exempt automatically, and only real challenge containers are contained:
/// <list type="bullet">
///   <item>challenge → challenge (in-set → in-set): the lateral pivot — DROP.</item>
///   <item>challenge → 169.254/16, 10/8, 172.16/12, 192.168/16: metadata/private — DROP.</item>
///   <item>everything else (checker→target, sidecar→target, container→internet): allowed.</item>
/// </list></para>
///
/// <para>gzctf isn't <c>--network host</c>, so each pass it spawns a short-lived
/// privileged helper in the host netns to (re)apply an idempotent ruleset, reusing
/// the "gzctf launches helper containers" pattern. Periodic so it survives a dockerd
/// restart (which flushes <c>DOCKER-USER</c>) and tracks containers as they churn.
/// No-ops on K8s (NetworkPolicy) or when <c>DockerConfig.EnforceEgressIsolation</c>
/// is false (then it tears the chain + set down).</para>
/// </summary>
public sealed class AdEgressIsolationService(
    IServiceScopeFactory scopeFactory,
    ILogger<AdEgressIsolationService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    internal const string Chain = "GZCTF_AD_ISO";
    internal const string Set = "gzctf_chal";
    private const string HelperImage = "alpine:3.21";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ApplyOnceAsync(stoppingToken); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "AdEgressIsolation: pass failed");
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ApplyOnceAsync(CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dockerProvider = scope.ServiceProvider.GetService<IContainerProvider<DockerClient, DockerMetadata>>();
        if (dockerProvider is null)
            return; // Kubernetes / no Docker provider → handled by NetworkPolicy

        var docker = dockerProvider.GetProvider();

        if (!dockerProvider.GetMetadata().Config.EnforceEgressIsolation)
        {
            await RunHelperAsync(docker, BuildTeardownScript(), token);
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Every live A&D per-team container + every KotH shared hill = the set of
        // "challenge containers" we contain. Ephemeral checker containers, gzctf, and
        // the sidecar are deliberately NOT in this set, so they stay unrestricted.
        var ips = await db.AdTeamServices
            .Where(t => t.Container != null && t.Container.IP != "")
            .Select(t => t.Container!.IP)
            .ToListAsync(token);
        ips.AddRange(await db.KothTargets
            .Where(t => t.Container != null && t.Container.IP != "")
            .Select(t => t.Container!.IP)
            .ToListAsync(token));

        var valid = ips.Where(IsValidIp).Distinct().ToList();
        if (valid.Count == 0)
            return; // nothing to contain yet

        await RunHelperAsync(docker, BuildRulesScript(valid), token);
        logger.SystemLog($"A&D egress isolation applied: {valid.Count} challenge container(s) contained",
            TaskStatus.Success, LogLevel.Debug);
    }

    /// <summary>
    /// Build the idempotent iptables+ipset script (pure — unit-tested). Populates the
    /// <see cref="Set"/> ipset with the challenge-container IPs, then rebuilds the
    /// <see cref="Chain"/>: allow established, DROP in-set→in-set (team↔team pivot)
    /// and in-set→metadata/private; everything else falls through to internet.
    /// </summary>
    internal static string BuildRulesScript(IEnumerable<string> challengeIps)
    {
        var sb = new StringBuilder();
        AppendPreamble(sb);
        sb.AppendLine($"ipset create {Set} hash:ip -exist");
        sb.AppendLine($"ipset flush {Set}");
        foreach (var ip in challengeIps.Where(IsValidIp).Distinct())
            sb.AppendLine($"ipset add {Set} {ip} -exist");
        sb.AppendLine($"\"$IPT\" -N {Chain} 2>/dev/null || true");
        sb.AppendLine($"\"$IPT\" -C DOCKER-USER -j {Chain} 2>/dev/null || \"$IPT\" -I DOCKER-USER -j {Chain}");
        sb.AppendLine($"\"$IPT\" -F {Chain}");
        sb.AppendLine($"\"$IPT\" -A {Chain} -m conntrack --ctstate ESTABLISHED,RELATED -j RETURN");
        // team↔team lateral pivot (both ends are challenge containers)
        sb.AppendLine($"\"$IPT\" -A {Chain} -m set --match-set {Set} src -m set --match-set {Set} dst -j DROP");
        // cloud metadata + private ranges, from any challenge container
        foreach (var cidr in AdEgressBaseline.PrivateAndLinkLocal)
            sb.AppendLine($"\"$IPT\" -A {Chain} -m set --match-set {Set} src -d {cidr} -j DROP");
        return sb.ToString();
    }

    internal static string BuildTeardownScript()
    {
        var sb = new StringBuilder();
        AppendPreamble(sb);
        sb.AppendLine($"\"$IPT\" -D DOCKER-USER -j {Chain} 2>/dev/null || true");
        sb.AppendLine($"\"$IPT\" -F {Chain} 2>/dev/null || true");
        sb.AppendLine($"\"$IPT\" -X {Chain} 2>/dev/null || true");
        sb.AppendLine($"ipset destroy {Set} 2>/dev/null || true");
        return sb.ToString();
    }

    // Install tools, then select the iptables backend that owns DOCKER-USER
    // (Docker may have created it under legacy or nft).
    private static void AppendPreamble(StringBuilder sb)
    {
        sb.AppendLine("apk add --no-cache iptables ipset >/dev/null 2>&1 || true");
        sb.AppendLine("IPT=iptables");
        sb.AppendLine("if ! \"$IPT\" -S DOCKER-USER >/dev/null 2>&1; then");
        sb.AppendLine("  if iptables-legacy -S DOCKER-USER >/dev/null 2>&1; then IPT=iptables-legacy;");
        sb.AppendLine("  elif iptables-nft -S DOCKER-USER >/dev/null 2>&1; then IPT=iptables-nft; fi");
        sb.AppendLine("fi");
    }

    private async Task RunHelperAsync(DockerClient docker, string script, CancellationToken token)
    {
        string? id = null;
        try
        {
            CreateContainerResponse created;
            var pars = new CreateContainerParameters
            {
                Image = HelperImage,
                Cmd = ["sh", "-c", script],
                Labels = new Dictionary<string, string> { ["gzctf.role"] = "ad-egress-iso" },
                HostConfig = new HostConfig
                {
                    NetworkMode = "host", // host netns → host iptables / DOCKER-USER
                    Privileged = true,    // iptables + ipset need NET_ADMIN + module access
                    AutoRemove = false
                }
            };
            try { created = await docker.Containers.CreateContainerAsync(pars, token); }
            catch (DockerImageNotFoundException)
            {
                await docker.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = HelperImage }, null,
                    new Progress<JSONMessage>(_ => { }), token);
                created = await docker.Containers.CreateContainerAsync(pars, token);
            }
            id = created.ID;
            await docker.Containers.StartContainerAsync(id, new ContainerStartParameters(), token);

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            waitCts.CancelAfter(TimeSpan.FromSeconds(30));
            var wait = await docker.Containers.WaitContainerAsync(id, waitCts.Token);
            if (wait.StatusCode != 0)
                logger.LogWarning("AdEgressIsolation: helper exited {Code}", wait.StatusCode);
        }
        finally
        {
            if (id is not null)
                try { await docker.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }, CancellationToken.None); }
                catch { /* best-effort */ }
        }
    }

    private static bool IsValidIp(string? ip) =>
        !string.IsNullOrEmpty(ip) && IPAddress.TryParse(ip, out _);
}
