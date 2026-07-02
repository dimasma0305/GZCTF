# Running A&D challenges on the Kubernetes provider

## What works out of the box

The `AdContainerManager` uses the abstract `IContainerManager` interface, so
container launch + destroy + restart + resource caps all work on the
`KubernetesManager` implementation with no A&D-specific code:

- `CPUCount` / `MemoryLimit` / `StorageLimit` → pod resource requests/limits
- A&D challenges launch persistent pods that live for the whole game
- `AdContainerManager` reconcile loop launches missing pods on game
  start / late-join + tears them down on game end
- Player + admin API endpoints (`/api/Game/{id}/Ad/*`,
  `/api/edit/games/{id}/ad/*`) all work identically regardless of provider

## K8s-only operator steps

1. **Apply the RBAC** in `ad-k8s-rbac.yaml` and point the platform's
   kubeconfig at the `gzctf` ServiceAccount it creates. It grants exactly
   the verbs the provider's code paths use (pods incl. `pods/exec`,
   services, secrets, networkpolicies, plus cluster-scoped namespaces +
   `nodes:list`). Edit the namespace to match your
   `appsettings.ContainerProvider.KubernetesConfig.Namespace`.

2. **Apply the sample NetworkPolicy** in `ad-k8s-networkpolicy.yaml`. It
   enforces L4 isolation: A&D pods can talk to each other in the
   configured namespace but can't reach the gzctf control plane or
   external networks. Edit the `namespace:` field to match the same
   `KubernetesConfig.Namespace`.

   No manual pod labeling is needed: `KubernetesManager` automatically
   tags A&D / KotH pods (the ones delivered a flag via the pull sidecar)
   with `gzctf.gzti.me/AdEngine=true`, which is exactly what the sample
   policy's `podSelector` matches. Jeopardy pods are deliberately left
   unlabeled so their tighter egress isolation stays intact. (Earlier
   revisions of this file selected `gzctf/category: attack-defense`, a
   label the provider never set — the policy matched nothing and A&D
   traffic was silently blocked; that is fixed.)

## Known limitations vs the Docker provider

| Feature | Docker | Kubernetes (v1) |
|---|---|---|
| Container launch + destroy | ✓ | ✓ |
| Resource caps | ✓ | ✓ |
| L4 isolation (block outbound to control plane) | ✓ (iptables) | ✓ (NetworkPolicy from this file) |
| L2 isolation (block ARP spoofing between teams) | ✓ (ebtables) | ✗ (needs Multus or Cilium) |
| Per-game namespace (`ad-{gameId}`) | n/a (per-game Docker net) | ✗ (shares the configured namespace; future enhancement) |
| End-of-game snapshot download | ✓ | ✗ (no `docker commit` equivalent that doesn't require image-registry plumbing — future enhancement) |
| Single-NAT anti-Superman defense | ✓ (Phase 3) | n/a yet (Phase 3 is Docker-only initially) |

## Future enhancements (post-v1)

- Per-game k8s namespace + NetworkPolicy created/destroyed by
  `AdContainerManager` (matches the Docker per-game ad-net model)
- L2 isolation via Multus + macvlan or Cilium ARP-inspect rules
- Snapshot via building an image in a sidecar pod + pushing to the
  configured BuildRegistry (operator gets a `docker pull` instruction
  instead of a tarball download)

For events with strict requirements (L2 isolation, snapshot), use the
Docker provider until k8s parity lands.
