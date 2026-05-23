namespace GZCTF.Models.Response.Admin;

/// <summary>
/// Body returned by <c>GET /api/admin/MyIp</c> — drives the
/// "Check my IP" diagnostic on /admin/settings → Diagnostics.
/// </summary>
public sealed class MyIpInfoModel
{
    /// <summary>
    /// The IP gzctf currently sees for this request. After the
    /// ForwardedHeaders middleware: if the connection arrived via a
    /// trusted proxy, this is the client IP from X-Forwarded-For;
    /// otherwise it's the raw TCP source.
    /// </summary>
    public string DetectedIp { get; set; } = string.Empty;

    /// <summary>
    /// The raw TCP source IP of the request — the IP of whatever
    /// directly opened the socket to kestrel. Equal to
    /// <see cref="DetectedIp"/> when no proxy rewrite happened.
    /// </summary>
    public string RawConnectionIp { get; set; } = string.Empty;

    /// <summary>
    /// Verbatim value of the X-Forwarded-For header from the
    /// request, or empty if the upstream didn't send one. Comma-
    /// separated when multiple hops appended.
    /// </summary>
    public string ForwardedFor { get; set; } = string.Empty;

    /// <summary>
    /// True iff the ForwardedHeaders middleware accepted the
    /// upstream's X-Forwarded-For (i.e. the upstream's IP matched
    /// TrustedNetworks/TrustedProxies and DetectedIp now reflects
    /// the real client). False ⇒ the upstream isn't trusted; the
    /// header was ignored and DetectedIp is the proxy's own IP.
    /// </summary>
    public bool ProxyTrusted { get; set; }

    /// <summary>
    /// Resolved TrustedNetworks from the running config. Lets the
    /// operator confirm appsettings reached the live process and
    /// see which ranges would currently be trusted as a proxy.
    /// </summary>
    public List<string> TrustedNetworks { get; set; } = [];
}
