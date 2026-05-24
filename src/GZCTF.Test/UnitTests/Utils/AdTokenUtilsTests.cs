using System.Text;
using GZCTF.Utils;
using Xunit;

namespace GZCTF.Test.UnitTests.Utils;

/// <summary>
/// Pins the format contract for the A&amp;D team API token. The hint and
/// plaintext shape are surfaced to users and embedded in curl examples, so
/// changes here are user-visible and need an explicit signal.
/// </summary>
public class AdTokenUtilsTests
{
    private static byte[] DemoKey => Encoding.UTF8.GetBytes("test-xor-key-32-bytes-aaaaaaaaaa");

    [Fact]
    public void GeneratePlaintext_HasAdPrefix()
    {
        var t = AdTokenUtils.GeneratePlaintext();
        Assert.StartsWith(AdTokenUtils.TokenPrefix, t);
    }

    [Fact]
    public void GeneratePlaintext_IsBase64UrlLength()
    {
        // 32 raw bytes → 43 base64url chars (no padding); + "ad_" (3) = 46.
        var t = AdTokenUtils.GeneratePlaintext();
        Assert.Equal(46, t.Length);
    }

    [Fact]
    public void GeneratePlaintext_IsUniqueAcrossCalls()
    {
        var a = AdTokenUtils.GeneratePlaintext();
        var b = AdTokenUtils.GeneratePlaintext();
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void BuildHint_ShortString_ReturnsAsIs()
    {
        Assert.Equal("ad_short", AdTokenUtils.BuildHint("ad_short"));
    }

    [Fact]
    public void BuildHint_LongString_HasEllipsisInTheMiddle()
    {
        var hint = AdTokenUtils.BuildHint("ad_abcdefghijklmnop");
        Assert.Contains("…", hint);
        Assert.StartsWith("ad_abcd", hint);
        Assert.EndsWith("mnop", hint);
    }

    [Fact]
    public void BuildHint_PreservesPrefix()
    {
        var token = AdTokenUtils.GeneratePlaintext();
        var hint = AdTokenUtils.BuildHint(token);
        Assert.StartsWith(AdTokenUtils.TokenPrefix, hint);
    }

    [Fact]
    public void Hash_IsStableForSameInput()
    {
        var a = AdTokenUtils.Hash("ad_xyz", DemoKey);
        var b = AdTokenUtils.Hash("ad_xyz", DemoKey);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Hash_DiffersForDifferentInputs()
    {
        var a = AdTokenUtils.Hash("ad_xyz", DemoKey);
        var b = AdTokenUtils.Hash("ad_xyq", DemoKey);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_DiffersForDifferentKeys()
    {
        var k1 = Encoding.UTF8.GetBytes("key-one-aaaaaaaaaaaaaaaaaaaaaaa1");
        var k2 = Encoding.UTF8.GetBytes("key-two-aaaaaaaaaaaaaaaaaaaaaaa2");
        var a = AdTokenUtils.Hash("ad_xyz", k1);
        var b = AdTokenUtils.Hash("ad_xyz", k2);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_FallsBackToDefaultKey_WhenEmpty()
    {
        // Empty key shouldn't crash — the helper falls back to a fixed key
        // so unit-test envs without a configured XorKey still work.
        var hash = AdTokenUtils.Hash("ad_xyz", []);
        Assert.Equal(32, hash.Length);
    }

    [Fact]
    public void Hash_Output_IsSha256Length()
    {
        var hash = AdTokenUtils.Hash("anything", DemoKey);
        Assert.Equal(32, hash.Length);
    }
}
