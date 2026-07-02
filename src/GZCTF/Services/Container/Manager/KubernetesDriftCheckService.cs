using GZCTF.Repositories.Interface;
using GZCTF.Services.Container.Provider;
using k8s;

namespace GZCTF.Services.Container.Manager;

/// <summary>
/// Periodically lists every Pod/Service GZCTF's <see cref="KubernetesManager"/> has
/// ever labeled (<c>gzctf.gzti.me/ResourceId</c>) and cross-references them against
/// the live <see cref="Container"/> rows in the database. Anything present in K8s
/// but with no matching non-destroyed DB row is logged as an orphan — a container
/// GZCTF lost track of (a crash mid-teardown, a manual <c>kubectl delete</c> outside
/// GZCTF, a reconcile gap). This service only OBSERVES: it never deletes anything.
/// <para/>
/// Deliberately log-only rather than an active reaper: this fork's only exercised
/// K8s environment is a local k3d test cluster, not the live deployment (which runs
/// the Docker provider) — a background deletion loop that's never run against real
/// production traffic is not something to ship blind. Orphaned Services going
/// forward are now largely prevented by <see cref="KubernetesManager"/>'s
/// OwnerReferences on Service creation (K8s's own GC removes the Service once its
/// owning Pod is gone); this service's remaining value is surfacing whatever slips
/// past that — most notably leaked Pods, which have no owner of their own.
/// </summary>
public sealed class KubernetesDriftCheckService(
    IContainerProvider<Kubernetes, KubernetesMetadata> provider,
    IServiceScopeFactory scopeFactory,
    ILogger<KubernetesDriftCheckService> logger) : BackgroundService
{
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    const string ResourceIdLabel = "gzctf.gzti.me/ResourceId";

    // Log each orphan once per process lifetime rather than every pass — a resource
    // nobody is going to auto-clean stays "orphaned" forever, and this is the only
    // guard against that becoming a warning every 5 minutes for the life of a game.
    readonly HashSet<string> _alreadyWarned = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "KubernetesDriftCheck: pass failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    async Task CheckOnceAsync(CancellationToken token)
    {
        var client = provider.GetProvider();
        var ns = provider.GetMetadata().Config.Namespace;

        var pods = await client.CoreV1.ListNamespacedPodAsync(ns, labelSelector: ResourceIdLabel,
            cancellationToken: token);
        var services = await client.CoreV1.ListNamespacedServiceAsync(ns, labelSelector: ResourceIdLabel,
            cancellationToken: token);

        await using var scope = scopeFactory.CreateAsyncScope();
        var containerRepo = scope.ServiceProvider.GetRequiredService<IContainerRepository>();
        var liveIds = await containerRepo.GetLiveContainerIds(token);

        var orphanPods = pods.Items.Select(p => p.Metadata.Name).Where(n => !liveIds.Contains(n)).ToArray();
        var orphanServices = services.Items.Select(s => s.Metadata.Name).Where(n => !liveIds.Contains(n)).ToArray();

        foreach (var name in orphanPods)
            WarnOnce("pod", name);
        foreach (var name in orphanServices)
            WarnOnce("service", name);
    }

    void WarnOnce(string kind, string name)
    {
        if (!_alreadyWarned.Add($"{kind}/{name}"))
            return;

        logger.LogWarning(
            "KubernetesDriftCheck: orphaned {Kind} {Name} has no matching live container row in the database " +
            "(not deleted — observe only; investigate/clean up manually)", kind, name);
    }
}
