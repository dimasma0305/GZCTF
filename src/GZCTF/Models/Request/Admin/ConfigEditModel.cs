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
}
