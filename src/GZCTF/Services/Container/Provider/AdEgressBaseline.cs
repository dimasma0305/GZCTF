namespace GZCTF.Services.Container.Provider;

/// <summary>
/// Egress-deny CIDR baseline shared by the Kubernetes egress NetworkPolicy
/// (<c>KubernetesProvider</c>) and the Docker <c>DOCKER-USER</c> isolation rules
/// (<c>AdEgressIsolationService</c>), so a popped challenge container is contained
/// identically on both providers. <c>169.254.0.0/16</c> is the cloud-metadata
/// (link-local) block — the important one for RCE/SSRF credential theft; the rest
/// are private ranges (cluster pod/service + RFC1918).
/// </summary>
internal static class AdEgressBaseline
{
    public static readonly string[] PrivateAndLinkLocal =
        ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16"];
}
