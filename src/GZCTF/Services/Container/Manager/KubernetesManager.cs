// SPDX-License-Identifier: LicenseRef-GZCTF-Restricted
// Copyright (C) 2022-2025 GZTimeWalker
// Restricted Component - NOT under AGPLv3.
// See licenses/LicenseRef-GZCTF-Restricted.txt

using System.Net;
using GZCTF.Models.Internal;
using GZCTF.Services.Container.Provider;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace GZCTF.Services.Container.Manager;

public class KubernetesManager : IContainerManager
{
    /// <summary>
    /// Tiny image for the A&amp;D flag-writer sidecar (the pull poller). Stock
    /// busybox — has <c>wget</c>, multi-arch, no custom image to build/push.
    /// </summary>
    private const string FlagSidecarImage = "busybox:stable";

    /// <summary>How often the writer sidecar re-pulls the flag (seconds). Also the
    /// self-heal window if container-root deletes the file (VNs don't enforce RO).</summary>
    private const int FlagPollSeconds = 5;

    /// <summary>Sidecar-side mount path of the shared emptyDir (writer's RW view).
    /// The challenge mounts the same volume read-only at its flag directory.</summary>
    internal const string FlagDir = "/flagdir";

    /// <summary>emptyDir volume name shared between the challenge container
    /// (read-only) and the writer sidecar (read-write).</summary>
    internal const string FlagVolumeName = "ad-flag";

    /// <summary>Container name of the flag-writer sidecar (polls the pull URL).</summary>
    internal const string FlagWriterContainer = "gzctf-flag-writer";

    private readonly Kubernetes _client;
    private readonly ILogger<KubernetesManager> _logger;
    private readonly KubernetesMetadata _meta;

    public KubernetesManager(IContainerProvider<Kubernetes, KubernetesMetadata> provider,
        ILogger<KubernetesManager> logger)
    {
        _logger = logger;
        _meta = provider.GetMetadata();
        _client = provider.GetProvider();

        logger.SystemLog(StaticLocalizer[nameof(Resources.Program.ContainerManager_K8sMode)],
            TaskStatus.Success,
            LogLevel.Debug);
    }

    public async Task<Models.Data.Container?> CreateContainerAsync(ContainerConfig config,
        CancellationToken token = default)
    {
        var imageName = config.Image.Split("/").LastOrDefault()?.Split(":").FirstOrDefault();

        if (string.IsNullOrWhiteSpace(imageName))
        {
            _logger.SystemLog(
                StaticLocalizer[nameof(Resources.Program.ContainerManager_UnresolvedImageName), config.Image],
                TaskStatus.Failed, LogLevel.Warning);
            return null;
        }

        var authSecretName = _meta.AuthSecretNames.GetForImage(config.Image);
        var options = _meta.Config;

        var chalImage = imageName.ToValidRFC1123String("chal");

        var name = $"{chalImage}-{Guid.NewGuid().ToString("N")[..16]}";

        // GZCTF_FLAG is Per-team dynamic flag issued & audited by the platform.
        //
        // Compliance & Abuse Notice:
        //
        // These env vars are integral to anti-abuse, audit trails and license compliance under
        // the Restricted License (LicenseRef-GZCTF-Restricted). Unauthorized removal, renaming
        // or semantic alteration can indicate an attempt to bypass license terms or weaken
        // challenge isolation guarantees. Downstream extensions MUST preserve their semantics.
        // Modification without a valid authorization may be treated as misuse.
        //
        // References: NOTICE, LICENSE_ADDENDUM.txt, licenses/LicenseRef-GZCTF-Restricted.txt
        var envs = BuildContainerEnv(config);

        // A&D flag delivery (PULL model). Virtual nodes can't exec / initContainer
        // / subPath and don't enforce readOnly, so the flag-writer sidecar polls
        // config.FlagPullUrl and writes the flag into a shared emptyDir that the
        // challenge mounts read-only at the flag's *directory* (not subPath). Root
        // can still delete it, but the next poll (<= FlagPollSeconds) re-plants it.
        var pullFlag = !string.IsNullOrEmpty(config.FlagPullUrl) && !string.IsNullOrEmpty(config.FlagFilePath);
        var flagDir = pullFlag ? Path.GetDirectoryName(config.FlagFilePath!.Replace('\\', '/')) : null;
        var flagFile = pullFlag ? Path.GetFileName(config.FlagFilePath!) : null;
        if (pullFlag && string.IsNullOrEmpty(flagDir))
            flagDir = "/gzctf-flag"; // FlagFilePath must be under a subdir; guard root

        var challenge = new V1Container
        {
            Name = name,
            Image = config.Image,
            ImagePullPolicy = "Always",
            Env = envs,
            Ports = [new V1ContainerPort { ContainerPort = config.ExposedPort }],
            Resources = new V1ResourceRequirements
            {
                Limits = new Dictionary<string, ResourceQuantity>
                {
                    ["cpu"] = new($"{config.CPUCount * 100}m"),
                    ["memory"] = new($"{config.MemoryLimit}Mi"),
                    ["ephemeral-storage"] = new($"{config.StorageLimit}Mi")
                },
                Requests = new Dictionary<string, ResourceQuantity>
                {
                    ["cpu"] = new("10m"), ["memory"] = new("32Mi")
                }
            },
            VolumeMounts = pullFlag
                ? [new V1VolumeMount { Name = FlagVolumeName, MountPath = flagDir, ReadOnlyProperty = true }]
                : null
        };

        var containers = new List<V1Container> { challenge };
        if (pullFlag)
            containers.Add(new V1Container
            {
                Name = FlagWriterContainer,
                Image = FlagSidecarImage,
                // Poll the pull URL and rewrite the flag file in the shared volume.
                Command =
                [
                    "sh", "-c",
                    $"while true; do wget -qO {FlagDir}/{flagFile} \"$GZCTF_FLAG_URL\" 2>/dev/null; sleep {FlagPollSeconds}; done"
                ],
                Env = [new V1EnvVar { Name = "GZCTF_FLAG_URL", Value = config.FlagPullUrl }],
                VolumeMounts = [new V1VolumeMount { Name = FlagVolumeName, MountPath = FlagDir }],
                Resources = new V1ResourceRequirements
                {
                    Limits = new Dictionary<string, ResourceQuantity>
                    {
                        ["cpu"] = new("50m"), ["memory"] = new("16Mi")
                    },
                    Requests = new Dictionary<string, ResourceQuantity>
                    {
                        ["cpu"] = new("1m"), ["memory"] = new("4Mi")
                    }
                }
            });

        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = options.Namespace,
                Labels = new Dictionary<string, string>
                {
                    ["gzctf.gzti.me/ResourceId"] = name,
                    ["gzctf.gzti.me/Image"] = chalImage,
                    ["gzctf.gzti.me/TeamId"] = config.TeamId,
                    ["gzctf.gzti.me/UserId"] = config.UserId.ToString(),
                    ["gzctf.gzti.me/ChallengeId"] = config.ChallengeId.ToString(),
                    ["gzctf.gzti.me/NetworkMode"] = config.NetworkMode.ToString().ToLowerInvariant()
                }
            },
            Spec = new V1PodSpec
            {
                ImagePullSecrets =
                    authSecretName is null
                        ? Array.Empty<V1LocalObjectReference>()
                        : new List<V1LocalObjectReference> { new() { Name = authSecretName } },
                DnsPolicy = "None",
                DnsConfig = new() { Nameservers = options.Dns ?? ["223.5.5.5", "114.114.114.114"] },
                EnableServiceLinks = false,
                Volumes = pullFlag
                    ? [new V1Volume { Name = FlagVolumeName, EmptyDir = new V1EmptyDirVolumeSource() }]
                    : null,
                // No initContainers — virtual nodes don't support them; the writer
                // sidecar seeds the flag on its first poll.
                Containers = containers,
                RestartPolicy = "Never",
                AutomountServiceAccountToken = false
            }
        };

        try
        {
            pod = await _client.CreateNamespacedPodAsync(pod, options.Namespace, cancellationToken: token);
        }
        catch (HttpOperationException e)
        {
            _logger.LogCreationFailedWithHttpContext(name, e.Response.StatusCode, e.Response.Content);
            return null;
        }
        catch (Exception e)
        {
            _logger.LogErrorMessage(e,
                StaticLocalizer[nameof(Resources.Program.ContainerManager_ContainerCreationFailed), name]);
            return null;
        }

        if (pod is null)
        {
            _logger.SystemLog(
                StaticLocalizer[nameof(Resources.Program.ContainerManager_ContainerInstanceCreationFailed),
                    config.Image.Split("/").LastOrDefault() ?? ""], TaskStatus.Failed,
                LogLevel.Warning);
            return null;
        }

        // Service is needed for port mapping
        var service = new V1Service
        {
            ApiVersion = "v1",
            Kind = "Service",
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = _meta.Config.Namespace,
                Labels = new Dictionary<string, string> { ["gzctf.gzti.me/ResourceId"] = name }
            },
            Spec = new V1ServiceSpec
            {
                Type = _meta.ExposePort ? "NodePort" : "ClusterIP",
                Ports = [new V1ServicePort { Port = config.ExposedPort, TargetPort = config.ExposedPort }],
                Selector = new Dictionary<string, string> { ["gzctf.gzti.me/ResourceId"] = name }
            }
        };

        try
        {
            service = await _client.CoreV1.CreateNamespacedServiceAsync(service, _meta.Config.Namespace,
                cancellationToken: token);
        }
        catch (HttpOperationException e)
        {
            try
            {
                // remove the pod if service creation failed, ignore the error
                await _client.CoreV1.DeleteNamespacedPodAsync(name, _meta.Config.Namespace, cancellationToken: token);
            }
            catch
            {
                // ignored
            }

            _logger.LogServiceCreationFailedWithHttpContext(name, e.Response.StatusCode, e.Response.Content);
            return null;
        }
        catch (Exception e)
        {
            try
            {
                // remove the pod if service creation failed, ignore the error
                await _client.CoreV1.DeleteNamespacedPodAsync(name, _meta.Config.Namespace, cancellationToken: token);
            }
            catch
            {
                // ignored
            }

            _logger.LogErrorMessage(e,
                StaticLocalizer[nameof(Resources.Program.ContainerManager_ServiceCreationFailed), name]);
            return null;
        }

        var container = new Models.Data.Container
        {
            ContainerId = name,
            Image = config.Image,
            Port = config.ExposedPort,
            IP = service.Spec.ClusterIP,
            IsProxy = !_meta.ExposePort,
            // No tracking for k8s-managed containers
            Status = ContainerStatus.Running
        };

        if (!_meta.ExposePort)
            return container;

        container.PublicIP = _meta.PublicEntry;
        container.PublicPort = service.Spec.Ports[0].NodePort;

        return container;
    }

    public async Task DestroyContainerAsync(Models.Data.Container container, CancellationToken token = default)
    {
        try
        {
            await _client.CoreV1.DeleteNamespacedServiceAsync(container.ContainerId, _meta.Config.Namespace,
                cancellationToken: token);
            await _client.CoreV1.DeleteNamespacedPodAsync(container.ContainerId, _meta.Config.Namespace,
                cancellationToken: token);
        }
        catch (HttpOperationException e)
        {
            if (e.Response.StatusCode == HttpStatusCode.NotFound)
            {
                container.Status = ContainerStatus.Destroyed;
                return;
            }

            _logger.LogDeletionFailedWithHttpContext(container.LogId, e.Response.StatusCode, e.Response.Content);
        }
        catch (Exception e)
        {
            _logger.LogErrorMessage(e,
                StaticLocalizer[nameof(Resources.Program.ContainerManager_ContainerDeletionFailed),
                    container.LogId]);
            return;
        }

        container.Status = ContainerStatus.Destroyed;
    }

    private static IList<V1EnvVar> BuildContainerEnv(ContainerConfig config)
    {
        var envs = new List<V1EnvVar>(5)
        {
            new() { Name = "GZCTF_TEAM_ID", Value = config.TeamId },
            new() { Name = "GZCTF_USER_ID", Value = config.UserId.ToString() },
            new() { Name = "GZCTF_CHALLENGE_ID", Value = config.ChallengeId.ToString() }
        };

        if (config.GameId is int gameId)
            envs.Add(new V1EnvVar { Name = "GZCTF_GAME_ID", Value = gameId.ToString() });

        if (!string.IsNullOrWhiteSpace(config.Flag))
            envs.Add(new V1EnvVar { Name = "GZCTF_FLAG", Value = config.Flag });

        // A&D: tell the challenge where to read the per-tick flag (the read-only
        // pull volume). Challenges should read $GZCTF_FLAG_FILE (fallback /flag).
        if (!string.IsNullOrEmpty(config.FlagFilePath))
            envs.Add(new V1EnvVar { Name = "GZCTF_FLAG_FILE", Value = config.FlagFilePath });

        return envs;
    }

    public Task<Models.Response.Admin.ContainerStatsModel?> GetStatsAsync(
        Models.Data.Container container, CancellationToken token = default)
    {
        // Live container stats in the K8s path would go through
        // metrics-server / the metrics.k8s.io API, which is not yet wired.
        // Return null so the admin UI shows "—" rather than misleading data.
        return Task.FromResult<Models.Response.Admin.ContainerStatsModel?>(null);
    }
}
