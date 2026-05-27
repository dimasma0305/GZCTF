using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using GZCTF.Models.Internal;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace GZCTF.Services.Container.Provider;

public class KubernetesMetadata : ContainerProviderMetadata
{
    /// <summary>
    /// The secret names for registry authentication
    /// </summary>
    public RegistrySet<string> AuthSecretNames { get; set; } = new();

    /// <summary>
    /// Host IP address
    /// </summary>
    public string HostIp { get; set; } = string.Empty;

    /// <summary>
    /// Kubernetes Configuration
    /// </summary>
    public KubernetesConfig Config { get; set; } = new();
}

public class KubernetesProvider : IContainerProvider<Kubernetes, KubernetesMetadata>
{
    private readonly Kubernetes _kubernetesClient;
    private readonly KubernetesMetadata _kubernetesMetadata;

    private readonly string? _flagPullHost;
    private readonly int _flagPullPort;

    public KubernetesProvider(IOptions<RegistrySet<RegistryConfig>> registries, IOptions<ContainerProvider> options,
        IConfiguration configuration, ILogger<KubernetesProvider> logger)
    {
        _kubernetesMetadata = new()
        {
            Config = options.Value.KubernetesConfig ?? new(),
            PortMappingType = options.Value.PortMappingType,
            PublicEntry = options.Value.PublicEntry
        };

        // The A&D flag-writer sidecar pulls rotating flags from this host:port
        // (must be an IP — see Ad:FlagPullBaseUrl). It's the only egress an A&D
        // pod strictly needs, so both the open and isolated egress policies
        // carve out a dedicated allow-rule for it. Without this, an isolated
        // (deny-all-egress) A&D challenge could never receive its flag.
        if (Uri.TryCreate(configuration["Ad:FlagPullBaseUrl"], UriKind.Absolute, out var fp)
            && System.Net.IPAddress.TryParse(fp.Host, out _))
        {
            _flagPullHost = fp.Host;
            _flagPullPort = fp.Port;
        }

        KubernetesClientConfiguration config;

        if (!string.IsNullOrWhiteSpace(_kubernetesMetadata.Config.KubeConfig) &&
            File.Exists(_kubernetesMetadata.Config.KubeConfig))
        {
            config = KubernetesClientConfiguration.BuildConfigFromConfigFile(_kubernetesMetadata.Config.KubeConfig);
        }
        else if (KubernetesClientConfiguration.IsInCluster())
        {
            // use ServiceAccount token if running in cluster and no kube-config is provided
            config = KubernetesClientConfiguration.InClusterConfig();
        }
        else
        {
            logger.SystemLog(StaticLocalizer[nameof(Resources.Program.ContainerProvider_KubernetesConfigLoadFailed),
                _kubernetesMetadata.Config.KubeConfig]);
            throw new FileNotFoundException(_kubernetesMetadata.Config.KubeConfig);
        }

        _kubernetesMetadata.HostIp = new Uri(config.Host).Host;
        _kubernetesClient = new Kubernetes(config);

        try
        {
            InitKubernetes(registries.Value);
        }
        catch (Exception e)
        {
            logger.LogErrorMessage(e,
                StaticLocalizer[nameof(Resources.Program.ContainerProvider_KubernetesInitFailed), config.Host]);
            ExitWithFatalMessage(
                StaticLocalizer[nameof(Resources.Program.ContainerProvider_KubernetesInitFailed), config.Host]);
        }

        logger.SystemLog(StaticLocalizer[nameof(Resources.Program.ContainerProvider_KubernetesInited), config.Host],
            TaskStatus.Success,
            LogLevel.Debug);
    }

    public Kubernetes GetProvider() => _kubernetesClient;

    public KubernetesMetadata GetMetadata() => _kubernetesMetadata;

    private void InitKubernetes(RegistrySet<RegistryConfig> registries)
    {
        if (_kubernetesClient.CoreV1.ListNamespace().Items
            .All(ns => ns.Metadata.Name != _kubernetesMetadata.Config.Namespace))
            _kubernetesClient.CoreV1.CreateNamespace(
                new() { Metadata = new() { Name = _kubernetesMetadata.Config.Namespace } });

        // create network policies (replace if exists)
        EnsureNetworkPolicy(OpenNetworkPolicy);
        EnsureNetworkPolicy(IsolatedNetworkPolicy);

        // create auth secrets for registries
        foreach (var registry in registries.Where(registry => registry.Value.Valid))
            InsertRegistrySecret(registry.Key, registry.Value);
    }

    private void EnsureNetworkPolicy(V1NetworkPolicy policy)
    {
        var policyName = policy.Metadata.Name;
        try
        {
            _kubernetesClient.NetworkingV1.ReplaceNamespacedNetworkPolicy(policy, policyName,
                _kubernetesMetadata.Config.Namespace);
        }
        catch
        {
            _kubernetesClient.NetworkingV1.CreateNamespacedNetworkPolicy(policy, _kubernetesMetadata.Config.Namespace);
        }
    }

    private const string IsolatedNetworkPolicyName = "gzctf-network-isolated";
    private const string OpenNetworkPolicyName = "gzctf-network-open";

    /// <summary>
    /// Baseline egress-deny CIDRs for "open" challenges: the cluster pod/service
    /// network (10/8) plus the rest of RFC1918 and link-local. Blocking
    /// 169.254.0.0/16 is the important one — it keeps an RCE/SSRF challenge from
    /// reaching the cloud metadata service (node IAM credential theft). Operators
    /// <b>add</b> deployment-specific ranges (e.g. their node network) via
    /// <see cref="KubernetesConfig.AllowCidr"/>; that list augments this baseline,
    /// it never replaces it, so the private ranges can't be accidentally re-opened.
    /// </summary>
    private static readonly string[] EgressDenyBaseline =
        ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16"];

    /// <summary>Egress allow-rule for the A&amp;D flag-pull endpoint (control-plane
    /// host:port), or empty when <c>Ad:FlagPullBaseUrl</c> isn't a usable IP. Both
    /// policies include it so a pod can always receive its flag regardless of how
    /// locked-down its egress is.</summary>
    private IEnumerable<V1NetworkPolicyEgressRule> FlagPullEgressRules() =>
        _flagPullHost is null
            ? []
            :
            [
                new V1NetworkPolicyEgressRule
                {
                    To = [new V1NetworkPolicyPeer { IpBlock = new() { Cidr = $"{_flagPullHost}/32" } }],
                    Ports = [new V1NetworkPolicyPort { Port = _flagPullPort.ToString() }]
                }
            ];

    /// <summary>Egress allow-rule for cluster DNS (kube-dns) so name resolution
    /// works even under the isolated policy — the resolver is the only in-cluster
    /// endpoint reachable.</summary>
    private static V1NetworkPolicyEgressRule DnsEgressRule => new()
    {
        To =
        [
            new V1NetworkPolicyPeer
            {
                NamespaceSelector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string> { ["kubernetes.io/metadata.name"] = "kube-system" }
                },
                PodSelector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string> { ["k8s-app"] = "kube-dns" }
                }
            }
        ],
        Ports =
        [
            new V1NetworkPolicyPort { Protocol = "UDP", Port = "53" },
            new V1NetworkPolicyPort { Protocol = "TCP", Port = "53" }
        ]
    };

    /// <summary>
    /// Isolated Network Policy
    /// </summary>
    /// <remarks>
    ///  Blocks all outbound traffic except the A&amp;D flag-pull endpoint and
    ///  cluster DNS. This makes "isolated" A&amp;D challenges actually functional on
    ///  K8s (they can still receive flags) while reaching nothing else — no other
    ///  team, no node/control-plane, no kube-api/kubelet, no metadata, no internet.
    /// </remarks>
    private V1NetworkPolicy IsolatedNetworkPolicy =>
        new()
        {
            Metadata = new V1ObjectMeta { Name = IsolatedNetworkPolicyName },
            Spec = new V1NetworkPolicySpec
            {
                PodSelector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>
                    {
                        ["gzctf.gzti.me/NetworkMode"] = nameof(NetworkMode.Isolated).ToLowerInvariant()
                    }
                },
                PolicyTypes = ["Egress"],
                Egress = [.. FlagPullEgressRules(), DnsEgressRule]
            }
        };

    /// <summary>
    ///  Open Network Policy
    /// </summary>
    /// <remarks>
    ///  Allows outbound traffic to the public internet but denies the private +
    ///  link-local baseline (<see cref="EgressDenyBaseline"/>) plus any operator
    ///  <see cref="KubernetesConfig.AllowCidr"/> ranges — so a compromised "open"
    ///  challenge can't reach the cluster, cloud metadata, or other internal nets.
    /// </remarks>
    private V1NetworkPolicy OpenNetworkPolicy =>
        new()
        {
            Metadata = new() { Name = OpenNetworkPolicyName },
            Spec = new()
            {
                PodSelector = new V1LabelSelector
                {
                    MatchLabels = new Dictionary<string, string>
                    {
                        ["gzctf.gzti.me/NetworkMode"] = nameof(NetworkMode.Open).ToLowerInvariant()
                    }
                },
                PolicyTypes = ["Egress"],
                Egress =
                [
                    // Dedicated flag-pull + DNS allow-rules first, so they survive even
                    // when the operator adds their node/control-plane CIDR to AllowCidr
                    // (which the broad rule below would otherwise deny).
                    .. FlagPullEgressRules(),
                    DnsEgressRule,
                    new V1NetworkPolicyEgressRule
                    {
                        To =
                        [
                            new V1NetworkPolicyPeer
                            {
                                IpBlock = new()
                                {
                                    Cidr = "0.0.0.0/0",
                                    // Always deny the private/link-local baseline; AllowCidr (the
                                    // operator's node/control-plane ranges) augments, never replaces it.
                                    Except = EgressDenyBaseline
                                        .Concat(_kubernetesMetadata.Config.AllowCidr ?? [])
                                        .Distinct().ToList()
                                }
                            }
                        ]
                    }
                ]
            }
        };

    private void InsertRegistrySecret(string address, RegistryConfig registry)
    {
        var padding = $"GZCTF@{registry.UserName}@{address}".ToMD5String();
        var secretName = $"{registry.UserName}-{padding}".ToValidRFC1123String("secret");

        var auth = Codec.Base64.Encode($"{registry.UserName}:{registry.Password}");
        var dockerJsonObj = new DockerRegistryOptions(
            new Dictionary<string, DockerRegistryEntry> { [address] = new(auth, registry.UserName, registry.Password) }
        );

        var dockerJsonBytes =
            JsonSerializer.SerializeToUtf8Bytes(dockerJsonObj, AppJsonSerializerContext.Default.DockerRegistryOptions);
        var secret = new V1Secret
        {
            Metadata =
                new V1ObjectMeta { Name = secretName, NamespaceProperty = _kubernetesMetadata.Config.Namespace },
            Data = new Dictionary<string, byte[]> { [".dockerconfigjson"] = dockerJsonBytes },
            Type = "kubernetes.io/dockerconfigjson"
        };

        try
        {
            _kubernetesClient.CoreV1.ReplaceNamespacedSecret(secret, secretName,
                _kubernetesMetadata.Config.Namespace);
        }
        catch
        {
            _kubernetesClient.CoreV1.CreateNamespacedSecret(secret, _kubernetesMetadata.Config.Namespace);
        }

        if (!_kubernetesMetadata.AuthSecretNames.TryAdd(address, secretName))
            _kubernetesMetadata.AuthSecretNames[address] = secretName;
    }
}

[SuppressMessage("ReSharper", "InconsistentNaming")]
internal record DockerRegistryOptions(Dictionary<string, DockerRegistryEntry> auths);

[SuppressMessage("ReSharper", "InconsistentNaming")]
internal record DockerRegistryEntry(string auth, string? username, string? password);
