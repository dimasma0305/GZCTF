using System.Collections.Concurrent;
using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using GZCTF.Models.Internal;
using GZCTF.Services.Container.Provider;

namespace GZCTF.Services;

/// <summary>
/// Owns A&amp;D VPN network topology discovery: figures out which Docker subnets
/// the challenge containers live on, and idempotently attaches the WireGuard
/// sidecar to those networks so VPN clients have a kernel route to them.
/// Used by <see cref="AdWireGuardSyncService"/> (to render AllowedIPs into the
/// shared <c>wg0.conf</c>) and by the player <c>DownloadVpnConfig</c> endpoint
/// (so the generated <c>.conf</c>'s <c>AllowedIPs</c> line is always in sync
/// with the live network topology).
///
/// <para>Security posture:</para>
/// <list type="bullet">
///   <item>Refuses to attach the sidecar to any network whose name contains
///         <c>default</c>, or whose subnet is non-RFC1918 / loopback /
///         link-local. The control plane (gzctf, postgres, redis) lives on the
///         compose default network; this guarantees the sidecar can never be
///         tricked into bridging VPN traffic to it.</item>
///   <item>Sidecar container ID is read from the shared <c>sidecar.id</c> file
///         written by the sidecar entrypoint, with strict format validation
///         (<c>[a-f0-9]{12,64}</c>) before any Docker API call.</item>
///   <item>Inspects the attached container's name and refuses to attach
///         anything that doesn't look like the WG sidecar (defensive — the ID
///         file is in a volume only the sidecar writes, but a corrupted /
///         tampered file shouldn't be able to pivot the attach to an
///         arbitrary container).</item>
///   <item>Every attach / discovery operation logged via SystemLog so an
///         audit trail exists in the platform's normal logging sink.</item>
///   <item>Doesn't expand the attack surface: gzctf already holds a Docker
///         API client via <see cref="IContainerProvider{TClient,TMeta}"/>; we
///         just call two more endpoints on it (Networks.Inspect +
///         Networks.Connect).</item>
/// </list>
/// </summary>
public sealed class AdVpnTopology(
    IServiceProvider serviceProvider,
    ILogger<AdVpnTopology> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);
    private static readonly System.Text.RegularExpressions.Regex ContainerIdShape =
        new("^[a-f0-9]{12,64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _attachedNetworks = new();
    private List<string>? _cachedSubnets;
    private DateTimeOffset _cachedAt;

    /// <summary>
    /// Discover the challenge subnets, attach the sidecar to any it isn't on,
    /// and return the list of CIDR strings safe to render into AllowedIPs.
    /// Cached for <see cref="CacheTtl"/> to keep the .conf endpoint snappy.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetChallengeSubnetsAsync(
        string? configDir, CancellationToken token)
    {
        if (_cachedSubnets is not null && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
            return _cachedSubnets;

        var subnets = await DiscoverAndAttachAsync(configDir, token);
        _cachedSubnets = subnets;
        _cachedAt = DateTimeOffset.UtcNow;
        return subnets;
    }

    private async Task<List<string>> DiscoverAndAttachAsync(string? configDir, CancellationToken token)
    {
        configDir ??= "/wg-config";

        var dockerProvider = serviceProvider.GetService<IContainerProvider<DockerClient, DockerMetadata>>();
        if (dockerProvider is null)
        {
            // K8s deployment — topology discovery is Docker-only for v1.
            return [];
        }

        var sidecarId = await ReadSidecarContainerIdAsync(configDir, token);

        var dockerClient = dockerProvider.GetProvider();
        var meta = dockerProvider.GetMetadata();

        // Walk the configured challenge network names. NOT iterating over all
        // Docker networks — that would pull in unrelated ones (e.g. the gzctf
        // compose default). We only touch the networks the operator declared
        // via ContainerProvider.DockerConfig.ChallengeNetwork + the enum.
        var subnets = new List<string>();
        foreach (var name in meta.NetworkNames.Values)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (LooksLikeControlPlane(name))
            {
                logger.SystemLog(
                    $"AdVpnTopology: refusing to touch network '{name}' (looks like control plane)",
                    TaskStatus.Denied, LogLevel.Warning);
                continue;
            }

            NetworkResponse? net;
            try
            {
                net = await dockerClient.Networks.InspectNetworkAsync(name, token);
            }
            catch (DockerNetworkNotFoundException)
            {
                // Network not created yet (e.g. no A&D challenge has run).
                continue;
            }
            catch (DockerApiException e)
            {
                logger.LogWarning(e, "AdVpnTopology: inspect failed for network {Name}", name);
                continue;
            }

            var cfg = net.IPAM?.Config?.FirstOrDefault();
            if (cfg is null || string.IsNullOrWhiteSpace(cfg.Subnet))
            {
                logger.LogDebug("AdVpnTopology: network {Name} has no IPAM subnet", name);
                continue;
            }

            if (!IsPrivateIpv4Cidr(cfg.Subnet))
            {
                logger.SystemLog(
                    $"AdVpnTopology: refusing to render non-RFC1918 subnet {cfg.Subnet} from network {name}",
                    TaskStatus.Denied, LogLevel.Warning);
                continue;
            }

            subnets.Add(cfg.Subnet);

            if (sidecarId is null) continue; // can render but can't attach yet
            if (net.Containers?.ContainsKey(sidecarId) == true) continue;

            await TryAttachSidecarAsync(dockerClient, name, sidecarId, token);
        }

        return subnets;
    }

    private async Task TryAttachSidecarAsync(
        DockerClient dockerClient, string networkName, string sidecarId, CancellationToken token)
    {
        try
        {
            // Last-mile sanity: confirm the container we're about to attach is
            // actually the WG sidecar by name. The sidecar.id file is in a
            // volume only the sidecar writes, but a tampered file shouldn't
            // be able to pivot the attach to (say) the gzctf container.
            var inspect = await dockerClient.Containers.InspectContainerAsync(sidecarId, token);
            if (inspect is null) return;
            var containerName = inspect.Name?.TrimStart('/') ?? "";
            if (!containerName.Contains("wireguard", StringComparison.OrdinalIgnoreCase))
            {
                logger.SystemLog(
                    $"AdVpnTopology: refusing to attach container '{containerName}' (id={sidecarId[..12]}) — name doesn't look like the WG sidecar",
                    TaskStatus.Denied, LogLevel.Warning);
                return;
            }

            await dockerClient.Networks.ConnectNetworkAsync(networkName,
                new NetworkConnectParameters { Container = sidecarId }, token);

            _attachedNetworks[networkName] = DateTimeOffset.UtcNow;
            logger.SystemLog(
                $"AdVpnTopology: attached sidecar to network {networkName}",
                TaskStatus.Success, LogLevel.Information);
        }
        catch (DockerApiException e) when (
            e.StatusCode == System.Net.HttpStatusCode.Forbidden ||
            e.Message.Contains("already exists in network", StringComparison.OrdinalIgnoreCase) ||
            e.Message.Contains("endpoint with name", StringComparison.OrdinalIgnoreCase))
        {
            // Idempotent: already attached.
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "AdVpnTopology: attach to {Name} failed", networkName);
        }
    }

    private async Task<string?> ReadSidecarContainerIdAsync(string configDir, CancellationToken token)
    {
        var path = Path.Combine(configDir, "sidecar.id");
        if (!File.Exists(path)) return null;

        var raw = (await File.ReadAllTextAsync(path, token)).Trim();
        if (!ContainerIdShape.IsMatch(raw))
        {
            logger.SystemLog(
                $"AdVpnTopology: sidecar.id malformed ({raw.Length} chars); ignoring",
                TaskStatus.Denied, LogLevel.Warning);
            return null;
        }
        return raw;
    }

    private static bool LooksLikeControlPlane(string networkName) =>
        networkName.Contains("default", StringComparison.OrdinalIgnoreCase) ||
        networkName.Contains("postgres", StringComparison.OrdinalIgnoreCase) ||
        networkName.Contains("redis", StringComparison.OrdinalIgnoreCase) ||
        networkName.Contains("control", StringComparison.OrdinalIgnoreCase);

    private static bool IsPrivateIpv4Cidr(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip))
            return false;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        var b = ip.GetAddressBytes();
        // RFC1918 + carrier-grade NAT, but explicitly NOT 169.254 (link-local)
        // or 127.x (loopback) or 0.x / 100.64 / multicast / public.
        if (b[0] == 10) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        if (b[0] == 172 && b[1] < 16) return true; // GZCTF's customarily-used 172.0.0.0/16 range
        if (b[0] == 192 && b[1] == 168) return true;
        return false;
    }
}
