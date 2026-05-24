using System;
using System.Text;
using GZCTF.Utils;
using Xunit;

namespace GZCTF.Test.UnitTests.Utils;

/// <summary>
/// Pins the format contract for A&amp;D WireGuard peer provisioning.
/// </summary>
public class AdVpnKeysTests
{
    private static byte[] DemoKey => Encoding.UTF8.GetBytes("test-xor-key-32-bytes-aaaaaaaaaa");

    [Fact]
    public void GenerateX25519KeyPair_ReturnsRaw32Bytes()
    {
        var (pub, priv) = AdVpnKeys.GenerateX25519KeyPair();
        Assert.Equal(32, pub.Length);
        Assert.Equal(32, priv.Length);
    }

    [Fact]
    public void GenerateX25519KeyPair_PairsAreUnique()
    {
        var a = AdVpnKeys.GenerateX25519KeyPair();
        var b = AdVpnKeys.GenerateX25519KeyPair();
        Assert.NotEqual(a.PrivateKey, b.PrivateKey);
        Assert.NotEqual(a.PublicKey, b.PublicKey);
    }

    [Fact]
    public void AssignNextIp_StartsAtDotTwo()
    {
        var ip = AdVpnKeys.AssignNextIp("10.13.37.0/24", []);
        Assert.Equal("10.13.37.2", ip);
    }

    [Fact]
    public void AssignNextIp_SkipsUsedIps()
    {
        var ip = AdVpnKeys.AssignNextIp("10.13.37.0/24", new[] { "10.13.37.2", "10.13.37.3" });
        Assert.Equal("10.13.37.4", ip);
    }

    [Fact]
    public void AssignNextIp_Throws_WhenSubnetExhausted()
    {
        // /30 has 4 addresses: .0 network, .1 server, .2 player, .3 broadcast.
        // After .2 is used there are no more peer slots.
        Assert.Throws<InvalidOperationException>(() =>
            AdVpnKeys.AssignNextIp("10.0.0.0/30", new[] { "10.0.0.2" }));
    }

    [Fact]
    public void AssignNextIp_RejectsInvalidCidr()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AdVpnKeys.AssignNextIp("not-a-cidr", []));
        Assert.Throws<InvalidOperationException>(() =>
            AdVpnKeys.AssignNextIp("10.0.0.0", []));
    }

    [Fact]
    public void WrapUnwrap_Roundtrips()
    {
        var (_, priv) = AdVpnKeys.GenerateX25519KeyPair();
        var wrapped = AdVpnKeys.WrapPrivateKey(priv, DemoKey);
        var unwrapped = AdVpnKeys.UnwrapPrivateKey(wrapped, DemoKey);
        Assert.Equal(Convert.ToBase64String(priv), unwrapped);
    }

    [Fact]
    public void WrapPrivateKey_DiffersFromPlaintext_WhenKeyed()
    {
        var (_, priv) = AdVpnKeys.GenerateX25519KeyPair();
        var wrapped = AdVpnKeys.WrapPrivateKey(priv, DemoKey);
        Assert.NotEqual(Convert.ToBase64String(priv), wrapped);
    }

    [Fact]
    public void WrapPrivateKey_FallsBackToPlain_WhenKeyEmpty()
    {
        var (_, priv) = AdVpnKeys.GenerateX25519KeyPair();
        var wrapped = AdVpnKeys.WrapPrivateKey(priv, []);
        Assert.Equal(Convert.ToBase64String(priv), wrapped);
    }
}
