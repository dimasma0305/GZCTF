using System.Net;
using System.Text;
using System.Threading.Channels;
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
    // 30s (was 60s) to halve the worst-case window between a challenge container
    // becoming reachable and its egress-isolation rules being (re)applied. NOTE:
    // this only shrinks the window — the complete fix is to apply isolation at
    // container-create time (before it's reachable) + flush conntrack for any
    // flow that slipped through; that's an architectural follow-up, not done here.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    internal const string Chain = "GZCTF_AD_ISO";
    // Two ipsets so we can express the asymmetric KotH containment:
    //   gzctf_chal      — A&D per-team containers; both source AND destination of
    //                     the pivot DROP (no team→team), and source of the
    //                     metadata/private DROPs.
    //   gzctf_chal_koth — KotH shared hills; ONLY source of the metadata/private
    //                     DROPs (hills can't pivot out either), but EXCLUDED from
    //                     the dst-side pivot DROP — so an A&D foothold legitimately
    //                     attacking the hill (a valid play) reaches it.
    internal const string Set = "gzctf_chal";
    internal const string SetKoth = "gzctf_chal_koth";
    private const string HelperImage = "alpine:3.21";

    // On-demand re-apply trigger. Bounded(1)/drop-write: a launch only needs to
    // ensure one re-apply runs after it, and bursts coalesce into a single pass.
    private readonly Channel<byte> _trigger =
        Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <summary>
    /// Request an immediate egress-isolation re-apply — called after a challenge
    /// container launches/moves so its IP is contained within ~a second instead
    /// of waiting up to a full <see cref="Interval" />. Non-blocking; safe to
    /// call from the container-launch path.
    /// </summary>
    public void RequestReapply() => _trigger.Writer.TryWrite(0);

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

            // Wake on the periodic Interval OR an on-demand trigger (a container
            // launch/move), whichever comes first — so a freshly-reachable
            // container is contained within ~a second instead of up to Interval.
            try
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                waitCts.CancelAfter(Interval);
                await _trigger.Reader.WaitToReadAsync(waitCts.Token);
                while (_trigger.Reader.TryRead(out _)) { } // coalesce burst triggers
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Interval elapsed with no trigger — normal periodic pass.
            }
            catch (OperationCanceledException) { break; } // shutdown
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
        // Per-team A&D containers — contained on both directions of the pivot rule
        // (team A's foothold can't reach team B's foothold) and on the metadata /
        // private-range blocks.
        var adIps = await db.AdTeamServices
            .Where(t => t.Container != null && t.Container.IP != "")
            .Select(t => t.Container!.IP)
            .ToListAsync(token);
        // KotH shared hills — contained on the metadata/private-range blocks
        // (source side only) but EXCLUDED from the team↔team pivot drop so an A&D
        // foothold scripting the hill (a legit play) actually reaches it. The hill
        // itself still can't reach team A&D containers because the pivot rule still
        // sees its source IP as in-set (via gzctf_chal_koth → no path to A&D dst).
        var kothIps = await db.KothTargets
            .Where(t => t.Container != null && t.Container.IP != "")
            .Select(t => t.Container!.IP)
            .ToListAsync(token);

        var validAd = adIps.Where(IsValidIp).Distinct().ToList();
        var validKoth = kothIps.Where(IsValidIp).Distinct().ToList();
        if (validAd.Count == 0 && validKoth.Count == 0)
            return; // nothing to contain yet

        await RunHelperAsync(docker, BuildRulesScript(validAd, validKoth), token);
        logger.SystemLog(
            $"A&D egress isolation applied: {validAd.Count} A&D container(s) + {validKoth.Count} KotH hill(s) contained",
            TaskStatus.Success, LogLevel.Debug);
    }

    /// <summary>
    /// Build the idempotent iptables+ipset script (pure — unit-tested). Populates
    /// two ipsets — <see cref="Set"/> (A&amp;D per-team containers) and
    /// <see cref="SetKoth"/> (shared KotH hills) — then rebuilds the
    /// <see cref="Chain"/> with these rules in order:
    /// <list type="number">
    ///   <item>RETURN on ESTABLISHED/RELATED (replies to the checker, VPN, etc.).</item>
    ///   <item>DROP <c>{ad,koth} src → ad dst</c> — blocks team↔team pivot AND
    ///         hill→team backflow. KotH hills are NOT a valid pivot destination
    ///         either (you can attack the hill from a foothold but not vice versa).</item>
    ///   <item>DROP <c>{ad,koth} src → 169.254/16, 10/8, 172.16/12, 192.168/16</c>
    ///         — both A&amp;D and KotH containers are blocked from cloud metadata
    ///         + the rest of the private ranges (control plane, k8s API, …).</item>
    /// </list>
    /// Note: <c>ad src → koth dst</c> is deliberately NOT in this list, so a player
    /// attacking the hill from inside their own A&amp;D foothold reaches it (a legit
    /// play). The hill stays contained on egress because of rule (2)/(3).
    /// </summary>
    internal static string BuildRulesScript(IEnumerable<string> adContainerIps, IEnumerable<string> kothHillIps)
    {
        var sb = new StringBuilder();
        AppendPreamble(sb);

        // A&D set
        sb.AppendLine($"ipset create {Set} hash:ip -exist");
        sb.AppendLine($"ipset flush {Set}");
        foreach (var ip in adContainerIps.Where(IsValidIp).Distinct())
            sb.AppendLine($"ipset add {Set} {ip} -exist");

        // KotH set (separate; same hash:ip type)
        sb.AppendLine($"ipset create {SetKoth} hash:ip -exist");
        sb.AppendLine($"ipset flush {SetKoth}");
        foreach (var ip in kothHillIps.Where(IsValidIp).Distinct())
            sb.AppendLine($"ipset add {SetKoth} {ip} -exist");

        sb.AppendLine($"\"$IPT\" -N {Chain} 2>/dev/null || true");
        sb.AppendLine($"\"$IPT\" -C DOCKER-USER -j {Chain} 2>/dev/null || \"$IPT\" -I DOCKER-USER -j {Chain}");
        sb.AppendLine($"\"$IPT\" -F {Chain}");
        sb.AppendLine($"\"$IPT\" -A {Chain} -m conntrack --ctstate ESTABLISHED,RELATED -j RETURN");

        // team↔team lateral pivot + hill→team backflow (any in-set source → A&D dst)
        sb.AppendLine($"\"$IPT\" -A {Chain} -m set --match-set {Set} src     -m set --match-set {Set} dst -j DROP");
        sb.AppendLine($"\"$IPT\" -A {Chain} -m set --match-set {SetKoth} src -m set --match-set {Set} dst -j DROP");

        // cloud metadata + private ranges, from any contained container (A&D or KotH)
        foreach (var cidr in AdEgressBaseline.PrivateAndLinkLocal)
        {
            sb.AppendLine($"\"$IPT\" -A {Chain} -m set --match-set {Set} src     -d {cidr} -j DROP");
            sb.AppendLine($"\"$IPT\" -A {Chain} -m set --match-set {SetKoth} src -d {cidr} -j DROP");
        }
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
        sb.AppendLine($"ipset destroy {SetKoth} 2>/dev/null || true");
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
