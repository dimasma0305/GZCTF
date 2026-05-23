using System.Net;
using GZCTF.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace GZCTF.Models.Internal;

/// <summary>
/// Admin-editable proxy trust configuration. Mirrors the
/// <see cref="ForwardedOptions"/> shape but with primitive types
/// only (strings for the CIDR lists) so the reflection-based
/// ConfigService walker can save it cleanly.
///
/// <para>When <see cref="Enabled"/> is true, this config OVERRIDES
/// whatever's in appsettings.json's ForwardedOptions section.
/// When false, the appsettings section governs as before. Either
/// path eventually configures <c>ForwardedHeadersOptions</c> at
/// startup — changes here save immediately but only take effect
/// after the next service restart.</para>
/// </summary>
public class ProxyTrustConfig
{
    /// <summary>
    /// Master switch — set true via /admin/settings to make the rest
    /// of this config take precedence over the appsettings.json
    /// <c>ForwardedOptions</c> section. Default false so existing
    /// operators on appsettings.json keep working.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Honour X-Forwarded-For from trusted proxies.</summary>
    public bool ForwardXForwardedFor { get; set; } = true;

    /// <summary>Honour X-Forwarded-Host from trusted proxies.</summary>
    public bool ForwardXForwardedHost { get; set; }

    /// <summary>Honour X-Forwarded-Proto from trusted proxies.</summary>
    public bool ForwardXForwardedProto { get; set; }

    /// <summary>
    /// Maximum number of proxy hops to walk back through the
    /// X-Forwarded-For chain. 1 = just the immediate proxy.
    /// </summary>
    public int ForwardLimit { get; set; } = 1;

    /// <summary>
    /// Comma- or newline-separated list of CIDR ranges whose
    /// requests can set X-Forwarded-For. Use "0.0.0.0/0,::/0" to
    /// trust any upstream (only safe when gzctf is unreachable
    /// without going through your proxy).
    /// </summary>
    public string TrustedNetworksCsv { get; set; } = string.Empty;

    /// <summary>
    /// Comma- or newline-separated list of literal proxy IPs to
    /// trust. Usually unnecessary if <see cref="TrustedNetworksCsv"/>
    /// covers your subnet — but pinning a specific IP is sometimes
    /// preferred for stricter trust boundaries.
    /// </summary>
    public string TrustedProxiesCsv { get; set; } = string.Empty;

    /// <summary>
    /// Translate this admin-editable shape into the live
    /// <see cref="ForwardedHeadersOptions"/> that the framework
    /// middleware consumes. Mirror of <c>ForwardedOptions.</c>
    /// <c>ToForwardedHeadersOptions</c> — but driven by the
    /// primitive fields above instead of List&lt;string&gt;s.
    /// </summary>
    public void ToForwardedHeadersOptions(ForwardedHeadersOptions options)
    {
        var hdrs = ForwardedHeaders.None;
        if (ForwardXForwardedFor) hdrs |= ForwardedHeaders.XForwardedFor;
        if (ForwardXForwardedHost) hdrs |= ForwardedHeaders.XForwardedHost;
        if (ForwardXForwardedProto) hdrs |= ForwardedHeaders.XForwardedProto;
        options.ForwardedHeaders = hdrs;
        options.ForwardLimit = ForwardLimit;

        // Each entry is either "ip/prefix" (network) or "ip" (proxy).
        // Match the appsettings ForwardedOptions shape: networks go
        // to KnownIPNetworks, proxies (after DNS resolution) go to
        // KnownProxies. Whitespace + commas + newlines all separate.
        var netSeparators = new[] { ',', '\n', '\r', ';', ' ', '\t' };
        foreach (var raw in TrustedNetworksCsv.Split(netSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Trim().Split('/');
            if (parts.Length == 2 &&
                IPAddress.TryParse(parts[0], out var addr) &&
                int.TryParse(parts[1], out var prefixLen))
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(addr, prefixLen));
        }

        foreach (var raw in TrustedProxiesCsv.Split(netSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            // ResolveIP handles both literal IPs and hostnames —
            // matches the legacy ForwardedOptions.ToForwardedHeadersOptions
            // behaviour for parity.
            Array.ForEach(raw.Trim().ResolveIP(), ip => options.KnownProxies.Add(ip));
        }
    }
}
