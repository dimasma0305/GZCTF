using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using GZCTF.Extensions;
using GZCTF.Services.Cache;
using MemoryPack;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Utilities.Encoders;
using Serilog.Sinks.Grafana.Loki;

namespace GZCTF.Models.Internal;

/// <summary>
/// Ignore when saving automatically
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AutoSaveIgnoreAttribute : Attribute;

/// <summary>
/// Update cache when this property changes
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public sealed class CacheFlushAttribute(string cacheKey) : Attribute
{
    public string CacheKey { get; } = cacheKey;
}

/// <summary>
/// Account policy
/// </summary>
public class AccountPolicy
{
    /// <summary>
    /// Allow user registration
    /// </summary>
    public bool AllowRegister { get; set; } = true;

    /// <summary>
    /// Activate account upon registration
    /// </summary>
    public bool ActiveOnRegister { get; set; } = true;

    /// <summary>
    /// Use captcha verification
    /// </summary>
    [CacheFlush(CacheKey.CaptchaConfig)]
    public bool UseCaptcha { get; set; }

    /// <summary>
    /// Email confirmation required for registration, email change, and password recovery
    /// </summary>
    public bool EmailConfirmationRequired { get; set; }

    /// <summary>
    /// Email domain list, separated by commas
    /// </summary>
    public string EmailDomainList { get; set; } = string.Empty;

    /// <summary>
    /// Enable browser fingerprinting in Login/Register
    /// </summary>
    [CacheFlush(CacheKey.ClientConfig)]
    public bool EnableBrowserFingerprint { get; set; }

    /// <summary>
    /// Require each user on a team to log in from an IP not used by another teammate within the last 24 hours
    /// </summary>
    public bool RequireUniqueIpPerTeamUser { get; set; }

    /// <summary>
    /// Require each user on a team to have a browser fingerprint not used by another teammate within the last 24 hours
    /// </summary>
    public bool RequireUniqueFingerprintPerTeamUser { get; set; }

    /// <summary>
    /// Require each login IP to be globally unique: block login if ANY other user (not
    /// just a teammate) signed in from the same IP within the last 24 hours. Stronger
    /// than <see cref="RequireUniqueIpPerTeamUser"/>; when on, the per-team flag is
    /// redundant. Note: blocks unrelated users behind a shared NAT/campus IP — intended
    /// for events where each player must connect from a distinct address.
    /// </summary>
    public bool RequireUniqueIpGlobal { get; set; }

    /// <summary>
    /// Require each browser fingerprint to be globally unique: block login if ANY other
    /// user (not just a teammate) signed in with the same fingerprint within the last 24
    /// hours. Stronger than <see cref="RequireUniqueFingerprintPerTeamUser"/>; when on,
    /// the per-team flag is redundant.
    /// </summary>
    public bool RequireUniqueFingerprintGlobal { get; set; }
}

/// <summary>
/// External OAuth login providers (Google, Discord).
/// </summary>
/// <remarks>
/// Admin-editable from /admin/settings (DB-backed like <see cref="EmailConfig"/>); the
/// client secrets are XOR-obfuscated at rest by <see cref="AdminController.UpdateConfigs"/>
/// and blanked on read (the <c>HasXClientSecret</c> surrogates surface presence). The
/// authentication handlers read these via <c>IOptionsMonitor</c> and are tied to the config
/// reload token, so changes apply WITHOUT a restart. A provider's sign-in button appears and
/// its endpoints function only when BOTH its client id and secret are set. Can also be
/// bootstrapped from env (<c>OAuthConfig__GoogleClientId</c>, etc.).
/// </remarks>
public class OAuthConfig
{
    /// <summary>
    /// Google OAuth client id
    /// </summary>
    public string? GoogleClientId { get; set; }

    /// <summary>
    /// Google OAuth client secret (XOR-obfuscated at rest)
    /// </summary>
    public string? GoogleClientSecret { get; set; }

    /// <summary>
    /// Discord OAuth client id
    /// </summary>
    public string? DiscordClientId { get; set; }

    /// <summary>
    /// Discord OAuth client secret (XOR-obfuscated at rest)
    /// </summary>
    public string? DiscordClientSecret { get; set; }

    /// <summary>UI surrogate — true when <see cref="GoogleClientSecret"/> is set.
    /// Writable so the settings GET can carry presence across the transport-blanked copy.</summary>
    private bool? _hasGoogleSecret;
    [AutoSaveIgnore]
    public bool HasGoogleClientSecret
    {
        get => _hasGoogleSecret ?? !string.IsNullOrEmpty(GoogleClientSecret);
        set => _hasGoogleSecret = value;
    }

    /// <summary>UI surrogate — true when <see cref="DiscordClientSecret"/> is set.</summary>
    private bool? _hasDiscordSecret;
    [AutoSaveIgnore]
    public bool HasDiscordClientSecret
    {
        get => _hasDiscordSecret ?? !string.IsNullOrEmpty(DiscordClientSecret);
        set => _hasDiscordSecret = value;
    }

    [JsonIgnore]
    [AutoSaveIgnore]
    public bool GoogleEnabled =>
        !string.IsNullOrWhiteSpace(GoogleClientId) && !string.IsNullOrWhiteSpace(GoogleClientSecret);

    [JsonIgnore]
    [AutoSaveIgnore]
    public bool DiscordEnabled =>
        !string.IsNullOrWhiteSpace(DiscordClientId) && !string.IsNullOrWhiteSpace(DiscordClientSecret);
}

/// <summary>
/// Container policy
/// </summary>
public class ContainerPolicy
{
    /// <summary>
    /// Automatically destroy the oldest container when the limit is reached
    /// </summary>
    public bool AutoDestroyOnLimitReached { get; set; }

    /// <summary>
    /// User container limit, used to limit the number of exercise containers
    /// </summary>
    public int MaxExerciseContainerCountPerUser { get; set; } = 1;

    /// <summary>
    /// Default container lifetime in minutes
    /// </summary>
    [CacheFlush(CacheKey.ClientConfig)]
    [Range(1, 7200, ErrorMessageResourceName = nameof(Resources.Program.Model_OutOfRange),
        ErrorMessageResourceType = typeof(Resources.Program))]
    public int DefaultLifetime { get; set; } = 120;

    /// <summary>
    /// Extension duration for each renewal in minutes
    /// </summary>
    [CacheFlush(CacheKey.ClientConfig)]
    [Range(1, 7200, ErrorMessageResourceName = nameof(Resources.Program.Model_OutOfRange),
        ErrorMessageResourceType = typeof(Resources.Program))]
    public int ExtensionDuration { get; set; } = 120;

    /// <summary>
    /// Renewal window before container stops in minutes
    /// </summary>
    [CacheFlush(CacheKey.ClientConfig)]
    [Range(1, 360, ErrorMessageResourceName = nameof(Resources.Program.Model_OutOfRange),
        ErrorMessageResourceType = typeof(Resources.Program))]
    public int RenewalWindow { get; set; } = 10;
}

/// <summary>
/// Where the auto-build pipeline should push images after a successful
/// docker build. When disabled, images stay on the local daemon only
/// (fine for single-host setups where the runner shares the daemon).
/// When enabled, the builder retags the local image to
/// <c>{Server}/{Namespace?}/gzctf-auto/{gameId}/{slug}:{sha}</c> and
/// pushes — the runner then pulls from this registry.
///
/// <para>Password is stored XOR-obfuscated at rest (same pattern as
/// the API encryption keys in <see cref="X25519KeyPair"/>); plaintext
/// is never persisted to the Configs table.</para>
/// </summary>
public class BuildRegistryConfig
{
    /// <summary>
    /// Master switch. When false, the rest of the fields are ignored
    /// and built images stay local. UI hides the input fields behind
    /// this switch.
    /// </summary>
    public bool PushOnBuild { get; set; }

    /// <summary>
    /// Registry hostname, e.g. <c>ghcr.io</c>, <c>docker.io</c>,
    /// <c>registry.example.com:5000</c>. No scheme, no trailing slash.
    /// </summary>
    [MaxLength(255)]
    public string? Server { get; set; }

    /// <summary>
    /// Optional namespace prefix under <see cref="Server"/>, e.g.
    /// <c>myorg</c> in <c>ghcr.io/myorg/...</c>. Single segment, no
    /// slashes. Leave empty to push directly under the server root.
    /// </summary>
    [MaxLength(255)]
    public string? Namespace { get; set; }

    [MaxLength(255)]
    public string? Username { get; set; }

    /// <summary>
    /// XOR-obfuscated registry password / PAT. <see cref="HasPassword"/>
    /// surfaces presence to the UI without round-tripping the secret.
    /// </summary>
    [MaxLength(2048)]
    public string? Password { get; set; }

    /// <summary>
    /// True when <see cref="Password"/> is non-empty. Read by the UI
    /// so the password input can show a "(configured)" placeholder
    /// without echoing the obfuscated bytes back to the operator.
    /// </summary>
    /// <remarks>
    /// Writable so <c>AdminController.GetConfigs</c> can set it
    /// explicitly on its safe-copy response (the safe copy has
    /// Password blanked for transport, so the computed fallback
    /// would otherwise always evaluate false). When unset, falls
    /// back to <c>!string.IsNullOrEmpty(Password)</c>.
    /// </remarks>
    private bool? _hasPassword;
    [AutoSaveIgnore]
    public bool HasPassword
    {
        get => _hasPassword ?? !string.IsNullOrEmpty(Password);
        set => _hasPassword = value;
    }

    /// <summary>
    /// True when the registry is wired up enough to attempt a push.
    /// Server is the only hard requirement — anonymous pushes (no
    /// username/password) are valid against a local insecure registry.
    /// </summary>
    [AutoSaveIgnore]
    public bool IsConfigured => PushOnBuild && !string.IsNullOrEmpty(Server);
}

public class X25519KeyPair
{
    /// <summary>
    /// Public key
    /// </summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Private key
    /// </summary>
    public string PrivateKey { get; set; } = string.Empty;

    public void RegenerateKeys(byte[] xorKey)
    {
        var kp = CryptoUtils.GenerateX25519KeyPair();
        var privateKey = (X25519PrivateKeyParameters)kp.Private;
        var publicKey = (X25519PublicKeyParameters)kp.Public;
        var privateKeyBytes = Codec.Xor(privateKey.GetEncoded(), xorKey);
        PublicKey = Base64.ToBase64String(publicKey.GetEncoded());
        PrivateKey = Base64.ToBase64String(privateKeyBytes);
    }

    public string? Decrypt(string data, byte[] xorKey)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(xorKey);

        try
        {
            var encryptedData = Base64.Decode(data);
            var privateKeyBytes = Codec.Xor(Base64.Decode(PrivateKey), xorKey);
            var privateKey = new X25519PrivateKeyParameters(privateKeyBytes);

            return Encoding.UTF8.GetString(CryptoUtils.DecryptData(encryptedData, privateKey));
        }
        catch
        {
            // If decryption fails, return null
            return null;
        }
    }
}

public class Ed25519KeyPair
{
    /// <summary>
    /// Public key
    /// </summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Private key
    /// </summary>
    public string PrivateKey { get; set; } = string.Empty;

    public void RegenerateKeys(byte[] xorKey)
    {
        var kp = CryptoUtils.GenerateEd25519KeyPair();
        var privateKey = (Ed25519PrivateKeyParameters)kp.Private;
        var publicKey = (Ed25519PublicKeyParameters)kp.Public;
        var privateKeyBytes = Codec.Xor(privateKey.GetEncoded(), xorKey);
        PublicKey = Base64.ToBase64String(publicKey.GetEncoded());
        PrivateKey = Base64.ToBase64String(privateKeyBytes);
    }

    public string Sign(string data, byte[] xorKey, bool useUrlSafeBase64 = false)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(xorKey);

        var privateKeyBytes = Codec.Xor(Base64.Decode(PrivateKey), xorKey);
        var privateKey = new Ed25519PrivateKeyParameters(privateKeyBytes);
        return CryptoUtils.GenerateSignature(data, privateKey, SignAlgorithm.Ed25519, useUrlSafeBase64);
    }

    public bool Verify(string data, string signature, bool useUrlSafeBase64 = false)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(signature);

        try
        {
            var publicKey = new Ed25519PublicKeyParameters(Base64.Decode(PublicKey));
            return CryptoUtils.VerifySignature(data, signature, publicKey, SignAlgorithm.Ed25519, useUrlSafeBase64);
        }
        catch
        {
            // If verification fails, return false
            return false;
        }
    }
}

/// <summary>
/// A context for signature operations, including signing and verifying
/// </summary>
public record SignatureContext(Ed25519KeyPair EncryptedKeyPair, byte[] XorKey)
{
    public string Sign(string data, bool urlSafe = true) =>
        EncryptedKeyPair.Sign(data, XorKey, urlSafe);

    public bool Verify(string data, string signature, bool urlSafe = true) =>
        EncryptedKeyPair.Verify(data, signature, urlSafe);
}

/// <summary>
/// Configs controlled by the backend
/// </summary>
public class ManagedConfig
{
    /// <summary>
    /// Api encryption configuration
    /// </summary>
    public X25519KeyPair ApiEncryption { get; set; } = new();

    /// <summary>
    /// Api token configuration
    /// </summary>
    public Ed25519KeyPair ApiToken { get; set; } = new();
}

/// <summary>
/// Global settings
/// </summary>
public class GlobalConfig
{
    /// <summary>
    /// Default site description
    /// </summary>
    public const string DefaultDescription = "GZ::CTF is an open source CTF platform";

    /// <summary>
    /// Platform prefix name
    /// </summary>
    [CacheFlush(CacheKey.Index)]
    [CacheFlush(CacheKey.ClientConfig)]
    public string Title { get; set; } = "GZ";

    /// <summary>
    /// Platform slogan
    /// </summary>
    [CacheFlush(CacheKey.ClientConfig)]
    public string Slogan { get; set; } = "Hack for fun not for profit";

    /// <summary>
    /// Site description information
    /// </summary>
    [CacheFlush(CacheKey.Index)]
    public string? Description { get; set; } = DefaultDescription;

    /// <summary>
    /// Footer information
    /// </summary>
    [CacheFlush(CacheKey.ClientConfig)]
    public string? FooterInfo { get; set; }

    /// <summary>
    /// Custom theme color
    /// </summary>
    [CacheFlush(CacheKey.ClientConfig)]
    public string? CustomTheme { get; set; }

    /// <summary>
    /// Use asymmetric encryption for API requests
    /// </summary>
    [CacheFlush(CacheKey.ClientConfig)]
    public bool ApiEncryption { get; set; }

    /// <summary>
    /// Platform logo hash
    /// </summary>
    [AutoSaveIgnore]
    public string? LogoHash { get; set; }

    /// <summary>
    /// Platform favicon hash
    /// </summary>
    [AutoSaveIgnore]
    public string? FaviconHash { get; set; }

    [JsonIgnore]
    public string? LogoUrl => string.IsNullOrEmpty(LogoHash) ? null : $"/assets/{LogoHash}/logo";

    /// <summary>
    /// Platform name, used for email and homepage rendering
    /// </summary>
    [JsonIgnore]
    public string Platform => string.IsNullOrEmpty(Title) ? "GZ::CTF" : $"{Title}::CTF";
}

/// <summary>
/// Client configuration
/// </summary>
[MemoryPackable]
public partial class ClientConfig
{
    /// <summary>
    /// Platform prefix name
    /// </summary>
    public string Title { get; set; } = "GZ";

    /// <summary>
    /// Platform slogan
    /// </summary>
    public string Slogan { get; set; } = "Hack for fun not for profit";

    /// <summary>
    /// Footer information
    /// </summary>
    public string? FooterInfo { get; set; }

    /// <summary>
    /// Custom theme color
    /// </summary>
    public string? CustomTheme { get; set; }

    /// <summary>
    /// The public key used for API requests
    /// </summary>
    public string? ApiPublicKey { get; set; }

    /// <summary>
    /// Platform logo URL
    /// </summary>
    public string? LogoUrl { get; set; }

    /// <summary>
    /// Container port mapping type
    /// </summary>
    public ContainerPortMappingType PortMapping { get; set; } = ContainerPortMappingType.Default;

    /// <summary>
    /// Default container lifetime in minutes
    /// </summary>
    public int DefaultLifetime { get; set; } = 120;

    /// <summary>
    /// Extension duration for each renewal in minutes
    /// </summary>
    public int ExtensionDuration { get; set; } = 120;

    /// <summary>
    /// Renewal window before container stops in minutes
    /// </summary>
    public int RenewalWindow { get; set; } = 10;

    /// <summary>
    /// Enable browser fingerprinting in Login/Register
    /// </summary>
    public bool EnableBrowserFingerprint { get; set; }

    /// <summary>
    /// Whether Google OAuth sign-in is configured and available
    /// </summary>
    public bool EnableGoogleAuth { get; set; }

    /// <summary>
    /// Whether Discord OAuth sign-in is configured and available
    /// </summary>
    public bool EnableDiscordAuth { get; set; }

    [JsonIgnore]
    public DateTimeOffset UpdateTimeUtc { get; set; } = DateTimeOffset.UtcNow;

    public static ClientConfig FromServiceProvider(IServiceProvider serviceProvider) =>
        FromConfigs(
            serviceProvider.GetRequiredService<IOptionsSnapshot<GlobalConfig>>().Value,
            serviceProvider.GetRequiredService<IOptionsSnapshot<ContainerPolicy>>().Value,
            serviceProvider.GetRequiredService<IOptionsSnapshot<ContainerProvider>>().Value,
            serviceProvider.GetRequiredService<IOptionsSnapshot<ManagedConfig>>().Value,
            serviceProvider.GetRequiredService<IOptionsSnapshot<AccountPolicy>>().Value,
            serviceProvider.GetRequiredService<IOptionsSnapshot<OAuthConfig>>().Value);

    private static ClientConfig FromConfigs(GlobalConfig globalConfig, ContainerPolicy containerPolicy,
        ContainerProvider containerProvider, ManagedConfig managedConfig, AccountPolicy accountPolicy,
        OAuthConfig oauthConfig) =>
        new()
        {
            Title = globalConfig.Title,
            Slogan = globalConfig.Slogan,
            FooterInfo = globalConfig.FooterInfo,
            CustomTheme = globalConfig.CustomTheme,
            LogoUrl = globalConfig.LogoUrl,
            ApiPublicKey = globalConfig.ApiEncryption ? managedConfig.ApiEncryption.PublicKey : null,
            PortMapping = containerProvider.PortMappingType,
            DefaultLifetime = containerPolicy.DefaultLifetime,
            ExtensionDuration = containerPolicy.ExtensionDuration,
            RenewalWindow = containerPolicy.RenewalWindow,
            EnableBrowserFingerprint = accountPolicy.EnableBrowserFingerprint,
            EnableGoogleAuth = oauthConfig.GoogleEnabled,
            EnableDiscordAuth = oauthConfig.DiscordEnabled
        };
}

#region Mail Config

public class SmtpConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 587;
    public bool BypassCertVerify { get; set; }
}

public class EmailConfig
{
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// SMTP password. XOR-obfuscated at rest by the
    /// <see cref="AdminController.UpdateConfigs"/> save path (same
    /// shape as <see cref="BuildRegistryConfig.Password"/>);
    /// <see cref="HasPassword"/> surfaces presence to the UI without
    /// round-tripping the secret.
    /// </summary>
    public string Password { get; set; } = string.Empty;
    public string? SenderAddress { get; set; } = string.Empty;
    public string? SenderName { get; set; } = string.Empty;
    public SmtpConfig? Smtp { get; set; } = new();

    /// <summary>UI surrogate — true when <see cref="Password"/> is
    /// set. Writable so <c>AdminController.GetConfigs</c> can carry
    /// the real value across the transport-blanked safe copy.</summary>
    private bool? _hasPassword;
    [AutoSaveIgnore]
    public bool HasPassword
    {
        get => _hasPassword ?? !string.IsNullOrEmpty(Password);
        set => _hasPassword = value;
    }

    /// <summary>UI surrogate — true when the minimum needed to send
    /// mail is present (host + sender address).</summary>
    [AutoSaveIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SenderAddress) &&
        !string.IsNullOrWhiteSpace(Smtp?.Host) &&
        Smtp.Port > 0;
}

#endregion

#region Container Provider

[JsonConverter(typeof(JsonStringEnumConverter<ContainerProviderType>))]
public enum ContainerProviderType
{
    Docker,
    Kubernetes
}

[JsonConverter(typeof(JsonStringEnumConverter<ContainerPortMappingType>))]
public enum ContainerPortMappingType
{
    /// Use default to map the container port to a random port on the host
    Default,

    /// Use platform proxy to map the container tcp to wss
    PlatformProxy
}

public class ContainerProvider
{
    public ContainerProviderType Type { get; set; } = ContainerProviderType.Docker;
    public ContainerPortMappingType PortMappingType { get; set; } = ContainerPortMappingType.Default;
    public bool EnableTrafficCapture { get; set; }
    public string PublicEntry { get; set; } = string.Empty;
    public KubernetesConfig? KubernetesConfig { get; set; }
    public DockerConfig? DockerConfig { get; set; }
}

public class DockerConfig
{
    public string Uri { get; set; } = string.Empty;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string? ChallengeNetwork { get; set; }

    /// <summary>
    /// Enforce A&amp;D egress isolation on the challenge bridges (default on) — installs
    /// <c>DOCKER-USER</c> rules so a popped challenge container can't pivot team→team
    /// or reach cloud metadata / private ranges (the same containment K8s gets from
    /// its egress NetworkPolicy). Legit paths (the checker, VPN ingress, internet on
    /// "open") are exempted. Set false to disable (the rules are removed next cycle).
    /// </summary>
    public bool EnforceEgressIsolation { get; set; } = true;
}

public class KubernetesConfig
{
    public string Namespace { get; set; } = "gzctf-challenges";
    public string KubeConfig { get; set; } = "kube-config.yaml";
    /// <summary>
    /// Extra egress-deny CIDRs for "open" challenges (e.g. the cluster's node /
    /// control-plane network). These <b>augment</b> the built-in private +
    /// link-local baseline (10/8, 172.16/12, 192.168/16, 169.254/16) — they do
    /// not replace it, so the baseline can't be accidentally re-opened.
    /// </summary>
    public string[]? AllowCidr { get; set; }
    public string[]? Dns { get; set; }

    /// <summary>
    /// imagePullPolicy for launched challenge/checker pods. Defaults to
    /// <c>Always</c> (assumes a registry, picks up rebuilt mutable tags).
    /// Set to <c>IfNotPresent</c> for single-node / air-gapped clusters that
    /// side-load images (e.g. <c>k3d image import</c>) instead of pulling.
    /// </summary>
    public string ImagePullPolicy { get; set; } = "Always";

    /// <summary>
    /// At startup, actively verify the CNI enforces NetworkPolicy by spawning a
    /// throwaway isolated-labeled pod and checking it CANNOT reach the kube-api
    /// ClusterIP. All A&amp;D network isolation depends on enforcement; a CNI that
    /// silently ignores NetworkPolicy (e.g. plain flannel) turns it into a no-op.
    /// Default on for Kubernetes; the probe is best-effort and never blocks boot.
    /// </summary>
    public bool VerifyNetworkPolicy { get; set; } = true;

    /// <summary>
    /// Image for the <see cref="VerifyNetworkPolicy"/> probe pod — needs a shell
    /// and busybox <c>nc</c>. Must be pullable or already on the node.
    /// </summary>
    public string NetworkProbeImage { get; set; } = "busybox:stable";
}

public class RegistrySet<T> : Dictionary<string, T>
    where T : class
{
    public T? GetForImage(string image)
    {
        if (string.IsNullOrWhiteSpace(image))
            return null;

        image = image.Contains("://") ? image : $"https://{image}";

        if (!Uri.TryCreate(image, UriKind.Absolute, out var uri) || uri.HostNameType == UriHostNameType.Unknown)
            return null;

        return TryGetValue(uri.Authority, out var cfg) ? cfg :
            TryGetValue(uri.Host, out var cfgHost) ? cfgHost : null;
    }
}

public class RegistryConfig
{
    public string? ServerAddress { get; set; }
    public string? UserName { get; set; }

    /// <summary>
    /// Registry password / PAT used when pulling private images.
    /// Stored XOR-obfuscated at rest via
    /// <see cref="AdminController.UpdateConfigs"/> (same shape as
    /// <see cref="BuildRegistryConfig.Password"/>);
    /// <see cref="HasPassword"/> is the UI presence surrogate.
    /// </summary>
    public string? Password { get; set; }

    public bool Valid => !string.IsNullOrEmpty(UserName) &&
                         !string.IsNullOrEmpty(Password);

    /// <summary>UI surrogate — true when <see cref="Password"/> is
    /// set. Writable so the safe copy can carry the real value.</summary>
    private bool? _hasPassword;
    [AutoSaveIgnore]
    public bool HasPassword
    {
        get => _hasPassword ?? !string.IsNullOrEmpty(Password);
        set => _hasPassword = value;
    }

    /// <summary>UI surrogate — server hostname is configured; auth
    /// fields may still be blank (anonymous pulls).</summary>
    [AutoSaveIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServerAddress);
}

#endregion

#region Captcha Provider

[JsonConverter(typeof(JsonStringEnumConverter<CaptchaProvider>))]
public enum CaptchaProvider
{
    None,
    HashPow,
    CloudflareTurnstile
}

public class HashPowConfig
{
    // How many leading zeros the hash should have
    private int _difficulty = 18;

    public int Difficulty
    {
        set => _difficulty = value;
        get => _difficulty = Math.Clamp(_difficulty, 8, 48);
    }
}

public class CaptchaConfig
{
    public CaptchaProvider Provider { get; set; }

    /// <summary>
    /// Server-side captcha secret (verified by the provider's
    /// validation endpoint). XOR-obfuscated at rest via
    /// <see cref="AdminController.UpdateConfigs"/>.
    /// </summary>
    public string? SecretKey { get; set; }

    /// <summary>Browser-side site key — public, served as-is.</summary>
    public string? SiteKey { get; set; }
    public HashPowConfig HashPow { get; set; } = new();

    /// <summary>UI surrogate — true when <see cref="SecretKey"/> is
    /// set. Writable so the safe copy can carry the real value
    /// across the transport-blanked response.</summary>
    private bool? _hasSecretKey;
    [AutoSaveIgnore]
    public bool HasSecretKey
    {
        get => _hasSecretKey ?? !string.IsNullOrEmpty(SecretKey);
        set => _hasSecretKey = value;
    }
}

#endregion

#region Telemetry

public class TelemetryConfig
{
    public PrometheusConfig Prometheus { get; set; } = new();
    public OpenTelemetryConfig OpenTelemetry { get; set; } = new();
    public AzureMonitorConfig AzureMonitor { get; set; } = new();
    public ConsoleConfig Console { get; set; } = new();

    [JsonIgnore]
    public bool Enable => Prometheus.Enable || OpenTelemetry.Enable || AzureMonitor.Enable || Console.Enable;
}

public class PrometheusConfig
{
    public bool Enable { get; set; }
    public bool TotalNameSuffixForCounters { get; set; }
}

public class OpenTelemetryConfig
{
    public bool Enable { get; set; }
    public OtlpExportProtocol Protocol { get; set; }
    public string? EndpointUri { get; set; }
}

public class AzureMonitorConfig
{
    public bool Enable { get; set; }
    public string? ConnectionString { get; set; }
}

public class ConsoleConfig
{
    public bool Enable { get; set; }
}

#endregion

public class GrafanaLokiOptions
{
    public bool Enable { get; set; }
    public string? EndpointUri { get; set; }
    public LokiLabel[]? Labels { get; set; }
    public string[]? PropertiesAsLabels { get; set; }
    public LokiCredentials? Credentials { get; set; }
    public string? Tenant { get; set; }
    public LogLevel? MinimumLevel { get; set; }
}

public class ForwardedOptions : ForwardedHeadersOptions
{
    // For historical configuration compatibility as we accept string
    public new List<string>? KnownIPNetworks { get; set; }
    public new List<string>? KnownProxies { get; set; }

    // Old properties for compatibility
    public new List<string>? KnownNetworks { get; set; }
    public List<string>? TrustedNetworks { get; set; }
    public List<string>? TrustedProxies { get; set; }

    public void ToForwardedHeadersOptions(ForwardedHeadersOptions options)
    {
        // assign the same value to the base class via reflection
        var type = typeof(ForwardedHeadersOptions);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var property in properties)
        {
            // skip the properties that are not being set directly
            // .NET 10 update: `KnownNetworks` is obsolete, needs to be skipped
            if (property.Name is nameof(KnownIPNetworks) or nameof(KnownProxies) or "KnownNetworks")
                continue;

            property.SetValue(options, property.GetValue(this));
        }

        // Handle KnownIPNetworks
        Action<string> addNetwork = networkString =>
        {
            // split the network into address and prefix length
            var parts = networkString.Split('/');
            if (parts.Length == 2 &&
                IPAddress.TryParse(parts[0], out var prefix) &&
                int.TryParse(parts[1], out var prefixLength))
                options.KnownIPNetworks.Add(new IPNetwork(prefix, prefixLength));
        };

        KnownIPNetworks?.ForEach(addNetwork);
        KnownNetworks?.ForEach(addNetwork);
        TrustedNetworks?.ForEach(addNetwork);

        // Handle KnownProxies
        Action<string> addProxies = proxy =>
            Array.ForEach(proxy.ResolveIP(), ip => options.KnownProxies.Add(ip));

        KnownProxies?.ForEach(addProxies);
        TrustedProxies?.ForEach(addProxies);
    }
}
