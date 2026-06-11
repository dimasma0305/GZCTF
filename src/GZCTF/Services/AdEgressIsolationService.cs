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

    // Active KotH leader-cooldown foothold blocks, keyed by challenge id. A just-
    // dethroned leader's VPN path to the hill is dropped on the WG sidecar, but its
    // own A&D foothold reaches the hill over the host bridge (egress intentionally
    // allows ad→koth as a legit play) — and only THIS host DOCKER-USER chain filters
    // bridge traffic. AdContainerManager sets/clears these at the KotH refresh
    // boundary and requests a reapply so the block tracks the (short) cooldown window.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, KothFootholdCooldown> _kothCooldowns =
        new();

    private sealed record KothFootholdCooldown(string[] FootholdIps, string HillIp);

    // Convergence memo for the empty/torn-down state: true once the chain has been flushed and
    // no rules are applied. Without it, the "nothing to contain" and "isolation disabled" paths
    // would re-spawn the privileged host-netns teardown helper on EVERY pass (~30s) forever on
    // idle installs (jeopardy-only, or A&D between games) — the common steady state. Loop-confined
    // (only read/written in ApplyOnceAsync, which runs solely on the single ExecuteAsync loop).
    private bool _chainTornDown;

    /// <summary>
    /// Block <paramref name="footholdIps"/> (the cooled-down leader's own A&amp;D
    /// container IPs) → <paramref name="hillIp"/> in the host egress chain until
    /// cleared. Idempotent; clears the entry when given nothing valid.
    /// </summary>
    public void SetKothFootholdCooldown(int challengeId, IEnumerable<string> footholdIps, string hillIp)
    {
        var ips = footholdIps.Where(IsValidIp).Distinct().ToArray();
        if (ips.Length == 0 || !IsValidIp(hillIp))
        {
            _kothCooldowns.TryRemove(challengeId, out _);
            return;
        }
        _kothCooldowns[challengeId] = new KothFootholdCooldown(ips, hillIp);
    }

    /// <summary>Lift a hill's foothold cooldown (call alongside the sidecar cooldown lift).</summary>
    public void ClearKothFootholdCooldown(int challengeId) => _kothCooldowns.TryRemove(challengeId, out _);

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
            // Tear the chain down once, then stay converged — don't re-spawn the helper every pass.
            if (!_chainTornDown)
            {
                await RunHelperAsync(docker, BuildTeardownScript(), token);
                _chainTornDown = true;
            }
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
        if (validAd.Count == 0 && validKoth.Count == 0 && _kothCooldowns.IsEmpty)
        {
            // Nothing left to contain — but the previous game's container IPs, control-plane
            // DROP rules, and any KotH cooldown rule are still live in GZCTF_AD_ISO. The old
            // code returned WITHOUT touching the chain, so those stale rules leaked host-wide
            // in DOCKER-USER (which filters ALL bridge traffic) until the next launch, surviving
            // IP reuse by a later game. Converge by flushing the chain to its empty state — the
            // same teardown the EnforceEgressIsolation=false path runs; a later launch reapplies.
            // ONE-SHOT (guarded): only flush on the transition into empty, not every idle pass,
            // else this churns a privileged helper container every ~30s forever on idle installs.
            if (!_chainTornDown)
            {
                await RunHelperAsync(docker, BuildTeardownScript(), token);
                _chainTornDown = true;
            }
            return;
        }

        // gzctf's own IPs — denied as a destination so a popped container can't
        // hit the control-plane API on the shared challenge bridge.
        var controlPlaneIps = await ResolveControlPlaneIpsAsync(docker, token);

        // Active KotH leader-cooldown foothold blocks (foothold IP → hill IP).
        var cooldownDrops = _kothCooldowns.Values
            .SelectMany(c => c.FootholdIps.Select(f => (Src: f, Dst: c.HillIp)))
            .ToList();

        await RunHelperAsync(docker, BuildRulesScript(validAd, validKoth, controlPlaneIps, cooldownDrops), token);
        _chainTornDown = false; // chain now has rules — re-arm the one-shot teardown for the next idle period
        logger.SystemLog(
            $"A&D egress isolation applied: {validAd.Count} A&D container(s) + {validKoth.Count} KotH hill(s) contained"
            + $"; {controlPlaneIps.Count} control-plane IP(s) blocked; {cooldownDrops.Count} KotH cooldown drop(s)",
            TaskStatus.Success, LogLevel.Debug);
    }

    /// <summary>
    /// Resolve the gzctf control-plane container's own IP addresses. Because
    /// <c>PortMappingType=PlatformProxy</c> attaches gzctf to the challenge
    /// bridge(s), a popped challenge container can reach the gzctf API same-subnet
    /// — a path the RFC1918 deny baseline misses whenever dockerd's address pool
    /// extends below <c>172.16.0.0/12</c> (e.g. a <c>172.0.0.0/10</c> pool). We
    /// re-resolve each pass (so the rule tracks container re-creation) and DROP
    /// challenge→these. Best-effort: a resolve failure means no extra rule this
    /// pass (no worse than before), logged so the gap stays visible.
    /// </summary>
    private async Task<List<string>> ResolveControlPlaneIpsAsync(DockerClient docker, CancellationToken token)
    {
        try
        {
            // Inside docker the container hostname defaults to its short id, which
            // InspectContainer accepts as an identifier.
            var self = await docker.Containers.InspectContainerAsync(Dns.GetHostName(), token);
            var networks = self.NetworkSettings?.Networks;
            var ips = networks is null
                ? new List<string>()
                : networks.Values.Select(n => n.IPAddress).Where(IsValidIp).Cast<string>().Distinct().ToList();
            if (ips.Count == 0)
                logger.LogWarning("AdEgressIsolation: could not resolve gzctf's own container IPs — "
                    + "challenge→control-plane DROP not applied this pass");
            return ips;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "AdEgressIsolation: control-plane IP resolution failed — "
                + "challenge→control-plane DROP not applied this pass");
            return [];
        }
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
    /// <para>Rule (4): DROP <c>{ad,koth} src → controlPlaneIps</c>. The gzctf
    /// container shares the challenge bridge under
    /// <c>PortMappingType=PlatformProxy</c>, so a popped container can reach the
    /// gzctf API <b>same-subnet</b> — which the RFC1918 baseline (3) misses whenever
    /// dockerd's address pool dips below <c>172.16.0.0/12</c> (e.g. a
    /// <c>172.0.0.0/10</c> pool puts gzctf at 172.0.x). Denying gzctf's own IPs
    /// closes that escape. ESTABLISHED replies to gzctf's outbound proxy connections
    /// are already RETURNed by rule (1); ad→hill and internet egress are different
    /// dsts and unaffected.</para>
    /// </summary>
    internal static string BuildRulesScript(
        IEnumerable<string> adContainerIps,
        IEnumerable<string> kothHillIps,
        IEnumerable<string>? controlPlaneIps = null,
        IEnumerable<(string Src, string Dst)>? kothCooldownDrops = null)
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

        // gzctf control-plane reach (rule 4): deny new connections from any
        // contained container to gzctf's own IPs. Catches the same-subnet API
        // reach the RFC1918 baseline misses when the docker pool dips below
        // 172.16/12. Per-IP (not the bridge subnet) so ad→hill stays allowed.
        foreach (var ip in (controlPlaneIps ?? Enumerable.Empty<string>()).Where(IsValidIp).Distinct())
        {
            sb.AppendLine($"\"$IPT\" -A {Chain} -m set --match-set {Set} src     -d {ip} -j DROP");
            sb.AppendLine($"\"$IPT\" -A {Chain} -m set --match-set {SetKoth} src -d {ip} -j DROP");
        }

        // KotH leader-cooldown (rule 5): per-tick DROP of a just-dethroned leader's
        // OWN A&D foothold → the freshly-reset hill. The VPN path is blocked on the WG
        // sidecar; this closes the host-bridge path the sidecar can't see (rule 2 only
        // covers ad→ad, and egress deliberately permits ad→koth as a legit play). The
        // hill is reset right before the cooldown so any prior flow is broken — these
        // catch the leader's NEW connection. Specific src→dst; removed when lifted.
        foreach (var (src, dst) in (kothCooldownDrops ?? Enumerable.Empty<(string, string)>()))
        {
            if (!IsValidIp(src) || !IsValidIp(dst)) continue;
            sb.AppendLine($"\"$IPT\" -A {Chain} -s {src} -d {dst} -j DROP");
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
        // iptables + ipset run from THIS (alpine) helper's filesystem against the host netns; they
        // aren't in bare alpine, so install them. If apk fails (air-gapped host / mirror down) AND
        // they aren't already present, ABORT LOUDLY (exit 3) — the old `apk add ... || true` swallowed
        // the failure and let the script run on with a MISSING binary and still exit 0, silently
        // no-op'ing the egress containment control with the operator none the wiser. (A baked helper
        // image with the tools preinstalled would remove the runtime apk dependency entirely — TODO.)
        sb.AppendLine("if ! command -v iptables >/dev/null 2>&1 || ! command -v ipset >/dev/null 2>&1; then");
        sb.AppendLine("  apk add --no-cache iptables ipset >/dev/null 2>&1 || true");
        sb.AppendLine("fi");
        sb.AppendLine("if ! command -v iptables >/dev/null 2>&1 || ! command -v ipset >/dev/null 2>&1; then");
        sb.AppendLine("  echo 'iptables/ipset unavailable (apk add failed and not preinstalled) - egress isolation NOT applied' >&2");
        sb.AppendLine("  exit 3");
        sb.AppendLine("fi");
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
                logger.LogWarning(
                    "AdEgressIsolation: helper exited {Code} — egress isolation rules may NOT have been applied this pass (exit 3 = iptables/ipset unavailable on the host; check connectivity to the apk mirror or bake a helper image with them preinstalled)",
                    wait.StatusCode);
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
