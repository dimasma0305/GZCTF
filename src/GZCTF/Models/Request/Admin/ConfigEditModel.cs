using GZCTF.Models.Internal;

namespace GZCTF.Models.Request.Admin;

/// <summary>
/// Global configuration update
/// </summary>
public class ConfigEditModel
{
    /// <summary>
    /// User policy
    /// </summary>
    public AccountPolicy? AccountPolicy { get; set; }

    /// <summary>
    /// Global configuration
    /// </summary>
    public GlobalConfig? GlobalConfig { get; set; }

    /// <summary>
    /// Game policy
    /// </summary>
    public ContainerPolicy? ContainerPolicy { get; set; }

    /// <summary>
    /// Auto-build image-push destination
    /// </summary>
    public BuildRegistryConfig? BuildRegistry { get; set; }

    /// <summary>
    /// SMTP relay used for email verification / password reset.
    /// Hot-reloadable via <see cref="MailSender"/>'s OptionsMonitor.
    /// </summary>
    public EmailConfig? Email { get; set; }

    /// <summary>
    /// Captcha provider for login / register flows.
    /// </summary>
    public CaptchaConfig? Captcha { get; set; }

    /// <summary>
    /// Pull credentials for a private image registry. Single-entry —
    /// covers the common "we host private images on ghcr.io" case.
    /// </summary>
    public RegistryConfig? Registry { get; set; }

    /// <summary>
    /// Reverse-proxy trust list (X-Forwarded-For / -Host / -Proto).
    /// When <see cref="ProxyTrustConfig.Enabled"/> is true, overrides
    /// appsettings.json's ForwardedOptions section. Changes save
    /// immediately but only take effect after the next service
    /// restart — ASP.NET's ForwardedHeadersMiddleware reads its
    /// options once at startup via <c>IOptions&lt;T&gt;</c> and
    /// doesn't observe later changes. The admin UI shows a
    /// restart-required alert next to this section.
    /// </summary>
    public ProxyTrustConfig? ProxyTrust { get; set; }

    /// <summary>
    /// Read-only view of the active container backend (Docker / Kubernetes).
    /// Sourced from startup config, not editable here — populated on GET and
    /// ignored on PUT.
    /// </summary>
    public ContainerProviderInfoModel? ContainerProvider { get; set; }
}

/// <summary>
/// Read-only summary of the configured container provider, so an admin can
/// tell at a glance whether challenges run on Docker or Kubernetes (and, for
/// K8s, in which namespace / with which pull policy). Set at startup via the
/// <c>ContainerProvider</c> config section; not editable from the settings UI.
/// </summary>
public sealed class ContainerProviderInfoModel
{
    /// <summary>The active backend: Docker or Kubernetes.</summary>
    public ContainerProviderType Type { get; set; }

    /// <summary>How challenge ports are exposed (Default / PlatformProxy).</summary>
    public ContainerPortMappingType PortMappingType { get; set; }

    /// <summary>Whether per-challenge traffic capture is enabled.</summary>
    public bool TrafficCapture { get; set; }

    /// <summary>K8s only: namespace challenge pods are created in.</summary>
    public string? KubernetesNamespace { get; set; }

    /// <summary>K8s only: imagePullPolicy applied to challenge / checker pods.</summary>
    public string? ImagePullPolicy { get; set; }
}
