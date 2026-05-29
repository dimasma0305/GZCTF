using System.Net;
using GZCTF.Services.Webhook;
using Xunit;

namespace GZCTF.Test.UnitTests.Services;

/// <summary>
/// Regression guard for the webhook SSRF fix: SendWebhookService must refuse to connect
/// to any non-public-unicast address (loopback, link-local incl. cloud metadata, RFC1918,
/// CGNAT, ULA, multicast, unspecified), including via IPv4-mapped IPv6. Relaxing this would
/// re-open SSRF from an operator-set DiscordWebhook URL to internal infra.
/// </summary>
public class SendWebhookSsrfGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]
    [InlineData("169.254.169.254")]        // cloud metadata (IMDS)
    [InlineData("169.254.1.1")]            // link-local
    [InlineData("10.0.0.5")]               // RFC1918
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]             // CGNAT 100.64/10
    [InlineData("0.0.0.0")]                // unspecified
    [InlineData("224.0.0.1")]              // multicast
    [InlineData("::1")]                    // IPv6 loopback
    [InlineData("fe80::1")]                // IPv6 link-local
    [InlineData("fc00::1")]                // IPv6 ULA
    [InlineData("fd12:3456::1")]           // IPv6 ULA
    [InlineData("::ffff:169.254.169.254")] // IPv4-mapped metadata
    [InlineData("::ffff:10.0.0.1")]        // IPv4-mapped private
    public void IsBlockedAddress_BlocksNonPublic(string ip)
        => Assert.True(SendWebhookService.IsBlockedAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("140.82.121.4")]           // github.com
    [InlineData("2606:4700:4700::1111")]   // cloudflare IPv6
    public void IsBlockedAddress_AllowsPublic(string ip)
        => Assert.False(SendWebhookService.IsBlockedAddress(IPAddress.Parse(ip)));
}
