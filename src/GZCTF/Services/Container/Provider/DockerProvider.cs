using Docker.DotNet;
using Docker.DotNet.BasicAuth;
using Docker.DotNet.Models;
using GZCTF.Models.Internal;
using Microsoft.Extensions.Options;

namespace GZCTF.Services.Container.Provider;

public class DockerMetadata : ContainerProviderMetadata
{
    /// <summary>
    /// Docker Configuration
    /// </summary>
    public DockerConfig Config { get; set; } = new();

    /// <summary>
    /// Docker Registry Authentication Configurations
    /// </summary>
    public RegistrySet<AuthConfig> AuthConfigs { get; set; } = new();

    /// <summary>
    /// Network names for different modes
    /// </summary>
    public Dictionary<NetworkMode, string> NetworkNames { get; set; } = new();

    /// <summary>
    /// Generate a unique container name based on the container configuration
    /// </summary>
    /// <param name="config"></param>
    /// <returns></returns>
    public static string GetName(GZCTF.Models.Internal.ContainerConfig config) =>
        $"{config.Image.Split("/").LastOrDefault()?.Split(":").FirstOrDefault()}_" +
        // Per-instance suffix from the flag; fall back to a random GUID when
        // there's no flag. MUST treat empty like null — A&D services leave
        // Flag empty (their flag rotates and lives in /flag), so without this
        // every flag-less container hashes MD5("")=d41d8… to the SAME name and
        // docker kills the incumbent on the next create (EXIT 137 churn).
        (string.IsNullOrEmpty(config.Flag) ? Guid.NewGuid().ToString("N") : config.Flag).ToMD5String()[..16];
}

public class DockerProvider : IContainerProvider<DockerClient, DockerMetadata>
{
    private readonly DockerClient _dockerClient;
    private readonly DockerMetadata _dockerMeta;

    public DockerProvider(IOptions<ContainerProvider> options, IOptions<RegistrySet<RegistryConfig>> registriesOptions,
        ILogger<DockerProvider> logger)
    {
        var config = options.Value.DockerConfig ?? new();
        var networkPrefix = string.IsNullOrWhiteSpace(config.ChallengeNetwork)
            ? "gzctf"
            : config.ChallengeNetwork.Trim();

        _dockerMeta = new()
        {
            Config = config,
            PortMappingType = options.Value.PortMappingType,
            NetworkNames =
                Enum.GetValues<NetworkMode>()
                    .ToDictionary(n => n,
                        m => $"{networkPrefix}-{m.ToString().ToLowerInvariant()}"),
            PublicEntry = options.Value.PublicEntry
        };

        // TODO: After Docker.DotNet.Enhanced 3.132.0 is adapted by testcontainers
        //
        // var builder = new DockerClientBuilder();
        //
        // if (!string.IsNullOrEmpty(_dockerMeta.Config.Uri))
        //     builder = builder.WithEndpoint(new Uri(_dockerMeta.Config.Uri));
        //
        // if (!string.IsNullOrEmpty(_dockerMeta.Config.UserName) && !string.IsNullOrEmpty(_dockerMeta.Config.Password))
        //     builder = builder.WithAuthProvider(new BasicAuthCredentials(_dockerMeta.Config.UserName,
        //         _dockerMeta.Config.Password));
        //
        // _dockerClient = builder.Build();

        Credentials? credentials = null;

        if (!string.IsNullOrEmpty(_dockerMeta.Config.UserName) && !string.IsNullOrEmpty(_dockerMeta.Config.Password))
            credentials = new BasicAuthCredentials(_dockerMeta.Config.UserName, _dockerMeta.Config.Password);

        DockerClientConfiguration cfg = string.IsNullOrEmpty(_dockerMeta.Config.Uri)
            ? new(credentials)
            : new(new Uri(_dockerMeta.Config.Uri), credentials);

        _dockerClient = cfg.CreateClient();

        var registries = registriesOptions.Value;

        foreach (var registry in registries.Where(registry =>
                     registry.Value.Valid))
        {
            var authConfig = new AuthConfig { Username = registry.Value.UserName, Password = registry.Value.Password };

            if (!_dockerMeta.AuthConfigs.TryAdd(registry.Key, authConfig))
                _dockerMeta.AuthConfigs[registry.Key] = authConfig;
        }

        EnsureNetworkCreated();

        logger.SystemLog(
            StaticLocalizer[nameof(Resources.Program.ContainerProvider_DockerInited),
                string.IsNullOrEmpty(_dockerMeta.Config.Uri) ? "localhost" : _dockerMeta.Config.Uri],
            TaskStatus.Success, LogLevel.Debug);
    }

    private void EnsureNetworkCreated()
    {
        // Snapshot the container's default gateway BEFORE we start connecting
        // to extra bridges — Docker promotes the most-recently-attached
        // network's gateway to be the default route. The challenge-isolated
        // bridge has enable_ip_masquerade=false (intentionally; isolated
        // challenges shouldn't reach the internet), so if it ends up as the
        // default route, gzctf's own outbound (github clone for repo
        // bindings, package mirrors, etc) breaks silently with timeouts.
        // We record the original default before any AttachSelfToNetwork and
        // restore it at the end via a privileged side-helper.
        var originalDefaultGateway = TryReadDefaultGateway();

        // create two network and attach self container to them (if in container)
        var networks = _dockerClient.Networks.ListNetworksAsync(new NetworksListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["name"] = _dockerMeta.NetworkNames.Values.ToDictionary(name => name, _ => true)
            }
        }).GetAwaiter().GetResult();

        EnsureOpenNetwork(networks);
        EnsureIsolatedNetwork(networks);

        // always try to attach to custom network if exists
        var customNetworkName = _dockerMeta.NetworkNames[NetworkMode.Custom];
        if (networks.Any(n => n.Name == customNetworkName))
            AttachSelfToNetwork(customNetworkName);

        // Restore the original default route now that all attachments are done.
        // Uses a short-lived privileged alpine container in host PID namespace
        // (the same helper pattern as AdEgressIsolationService) — runs nsenter
        // to enter our own netns and replace the default route. No NET_ADMIN
        // needed on gzctf itself; the helper has the cap.
        if (!string.IsNullOrEmpty(originalDefaultGateway))
            RestoreDefaultRouteAsync(originalDefaultGateway).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Read the in-container default route's gateway IP from /proc/net/route.
    /// Returns null if no default route is set or the file can't be read
    /// (e.g. running outside a container). Pure read of /proc — no special
    /// caps required.
    /// </summary>
    private static string? TryReadDefaultGateway()
    {
        try
        {
            foreach (var line in System.IO.File.ReadAllLines("/proc/net/route").Skip(1))
            {
                var fields = line.Split('\t');
                if (fields.Length < 3) continue;
                // /proc/net/route: Iface Destination Gateway Flags ...
                // We want the row where Destination=00000000 (= 0.0.0.0).
                if (fields[1] != "00000000") continue;
                // Gateway is little-endian hex. e.g. 010003AC = 172.0.3.1
                var gwHex = fields[2];
                if (gwHex.Length != 8) continue;
                var b1 = Convert.ToByte(gwHex.Substring(6, 2), 16);
                var b2 = Convert.ToByte(gwHex.Substring(4, 2), 16);
                var b3 = Convert.ToByte(gwHex.Substring(2, 2), 16);
                var b4 = Convert.ToByte(gwHex.Substring(0, 2), 16);
                return $"{b1}.{b2}.{b3}.{b4}";
            }
        }
        catch
        {
            // No /proc/net/route, malformed entries, or read perms — give up
            // silently. The route-restore step then no-ops.
        }
        return null;
    }

    /// <summary>
    /// Run a one-shot alpine container in the HOST PID + privileged so it can
    /// nsenter into our own netns and `ip route replace default via X`.
    /// </summary>
    private async Task RestoreDefaultRouteAsync(string gateway)
    {
        var selfId = Environment.GetEnvironmentVariable("HOSTNAME");
        if (string.IsNullOrEmpty(selfId)) return;

        // Validate the gateway as IPv4 dotted-quad before passing it to the
        // helper shell — paranoia against an accidental NUL/space byte
        // turning into a command-injection vector.
        if (!System.Net.IPAddress.TryParse(gateway, out var ip)
            || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return;

        try
        {
            var script =
                "apk add --no-cache iproute2 >/dev/null 2>&1 || true; " +
                $"PID=$(docker inspect {selfId} --format '{{{{.State.Pid}}}}' 2>/dev/null); " +
                "[ -n \"$PID\" ] || exit 0; " +
                $"nsenter -t \"$PID\" -n ip route replace default via {gateway} >/dev/null 2>&1 || true";

            var create = await _dockerClient.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = "alpine:3.21",
                Cmd = ["sh", "-c", script],
                HostConfig = new HostConfig
                {
                    Privileged = true,
                    PidMode = "host",
                    NetworkMode = "host",
                    AutoRemove = false,
                    Binds = ["/var/run/docker.sock:/var/run/docker.sock"]
                },
                Labels = new Dictionary<string, string> { ["gzctf.role"] = "default-route-fix" }
            });

            try
            {
                await _dockerClient.Containers.StartContainerAsync(create.ID, new ContainerStartParameters());
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await _dockerClient.Containers.WaitContainerAsync(create.ID, cts.Token);
            }
            finally
            {
                try { await _dockerClient.Containers.RemoveContainerAsync(create.ID,
                    new ContainerRemoveParameters { Force = true }); }
                catch { /* best-effort cleanup */ }
            }
        }
        catch
        {
            // Helper failed — log via the System log if you want, but don't
            // crash gzctf startup. Symptom (if it stays broken) is the same
            // timeout the operator already saw; nsenter from the host fixes it.
        }
    }

    private void EnsureOpenNetwork(IList<NetworkResponse> networks)
    {
        var openNetworkName = _dockerMeta.NetworkNames[NetworkMode.Open];
        if (networks.All(n => n.Name != openNetworkName))
        {
            _dockerClient.Networks.CreateNetworkAsync(new NetworksCreateParameters
            {
                Name = openNetworkName,
                Driver = "bridge",
                Attachable = true
            }).GetAwaiter().GetResult();
        }

        AttachSelfToNetwork(openNetworkName);
    }

    private void EnsureIsolatedNetwork(IList<NetworkResponse> networks)
    {
        var isolatedNetworkName = _dockerMeta.NetworkNames[NetworkMode.Isolated];
        if (networks.All(n => n.Name != isolatedNetworkName))
        {
            // Do not use internal network, it will disable the port mapping feature of docker
            // reference: https://github.com/moby/moby/issues/36174#issuecomment-2527195596
            _dockerClient.Networks.CreateNetworkAsync(new NetworksCreateParameters
            {
                Name = isolatedNetworkName,
                Driver = "bridge",
                Attachable = true,
                Options = new Dictionary<string, string>
                {
                    ["com.docker.network.bridge.enable_ip_masquerade"] = "false"
                }
            }).GetAwaiter().GetResult();
        }

        AttachSelfToNetwork(isolatedNetworkName);
    }

    private void AttachSelfToNetwork(string networkName)
    {
        var selfContainerId = Environment.GetEnvironmentVariable("HOSTNAME");
        if (string.IsNullOrEmpty(selfContainerId))
            return;

        try
        {
            _dockerClient.Networks.ConnectNetworkAsync(networkName,
                new NetworkConnectParameters { Container = selfContainerId }).GetAwaiter().GetResult();
        }
        catch
        {
            // ignore errors
        }
    }

    public DockerMetadata GetMetadata() => _dockerMeta;

    public DockerClient GetProvider() => _dockerClient;
}
