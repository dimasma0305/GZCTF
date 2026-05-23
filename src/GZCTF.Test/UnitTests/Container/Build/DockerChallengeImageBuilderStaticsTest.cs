using System;
using System.Text;
using GZCTF.Services.Container.Build;
using GZCTF.Utils;
using Xunit;

namespace GZCTF.Test.UnitTests.Container.Build;

/// <summary>
/// Coverage for the static helpers on
/// <see cref="DockerChallengeImageBuilder"/>. These don't touch
/// docker at all so they're cheap to unit-test, but they're
/// security-sensitive (log scrubbing) and operator-facing
/// (slug normalization) — worth explicit assertions.
/// </summary>
public class DockerChallengeImageBuilderStaticsTest
{
    #region NormalizeSlug

    [Theory]
    [InlineData("Hello World", "hello-world")]
    [InlineData("Cool_Challenge", "cool-challenge")]
    [InlineData("foo--bar---baz", "foo-bar-baz")]
    [InlineData("---trim-me---", "trim-me")]
    [InlineData("UPPERCASE", "uppercase")]
    public void NormalizeSlug_ProducesValidDockerTag(string input, string expected)
    {
        Assert.Equal(expected, DockerChallengeImageBuilder.NormalizeSlug(input));
    }

    [Fact]
    public void NormalizeSlug_AllPunctuation_FallsBackToConstant()
    {
        // After stripping non-alphanumerics, nothing's left — we need
        // a valid docker tag, so the builder substitutes a default.
        Assert.Equal("challenge", DockerChallengeImageBuilder.NormalizeSlug("!!!"));
    }

    #endregion

    #region HumanBytes

    [Theory]
    [InlineData(0UL, "0B")]
    [InlineData(512UL, "512B")]
    [InlineData(2048UL, "2KB")]
    [InlineData(5UL * 1024 * 1024, "5MB")]
    [InlineData(3UL * 1024 * 1024 * 1024, "3GB")]
    public void HumanBytes_FormatsAcrossUnitBoundaries(ulong bytes, string expected)
    {
        Assert.Equal(expected, DockerChallengeImageBuilder.HumanBytes(bytes));
    }

    #endregion

    #region ScrubSecrets

    [Theory]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789")]              // classic PAT (40 chars total: 4 prefix + 36)
    [InlineData("gho_abcdefghijklmnopqrstuvwxyz0123456789")]              // oauth
    [InlineData("ghs_abcdefghijklmnopqrstuvwxyz0123456789")]              // server-to-server
    [InlineData("ghr_abcdefghijklmnopqrstuvwxyz0123456789")]              // refresh
    public void ScrubSecrets_MasksGitHubPATShapes(string token)
    {
        var input = $"some log line containing {token} in the middle";

        var scrubbed = DockerChallengeImageBuilder.ScrubSecrets(input);

        Assert.DoesNotContain(token, scrubbed);
        Assert.Contains("***SCRUBBED***", scrubbed);
    }

    [Fact]
    public void ScrubSecrets_MasksFineGrainedPAT()
    {
        // github_pat_ prefix + ≥82 chars of [A-Za-z0-9_].
        var pat = "github_pat_" + new string('A', 82);
        var line = $"echo {pat}";

        var scrubbed = DockerChallengeImageBuilder.ScrubSecrets(line);

        Assert.DoesNotContain(pat, scrubbed);
        Assert.Contains("***SCRUBBED***", scrubbed);
    }

    [Fact]
    public void ScrubSecrets_MasksAWSAccessKey()
    {
        var key = "AKIAIOSFODNN7EXAMPLE";
        var line = $"AWS_ACCESS_KEY_ID={key}";

        var scrubbed = DockerChallengeImageBuilder.ScrubSecrets(line);

        Assert.DoesNotContain(key, scrubbed);
        Assert.Contains("***SCRUBBED***", scrubbed);
    }

    [Fact]
    public void ScrubSecrets_NoSecrets_ReturnsUnchanged()
    {
        var line = "just a regular build log line with no PATs";
        Assert.Equal(line, DockerChallengeImageBuilder.ScrubSecrets(line));
    }

    #endregion

    #region DecryptXorPassword

    [Fact]
    public void DecryptXorPassword_RoundTrip()
    {
        var plain = "super-secret-password";
        var key = "test-xor-key".ToUTF8Bytes();
        var encrypted = Convert.ToBase64String(Codec.Xor(plain.ToUTF8Bytes(), key));

        var decrypted = DockerChallengeImageBuilder.DecryptXorPassword(encrypted, key);

        Assert.Equal(plain, decrypted);
    }

    [Fact]
    public void DecryptXorPassword_EmptyKey_ReturnsStoredAsIs()
    {
        // No XorKey configured (test setups) → fall through to the
        // stored value unchanged. Documented defensive behavior.
        const string stored = "anything";
        Assert.Equal(stored, DockerChallengeImageBuilder.DecryptXorPassword(stored, []));
    }

    [Fact]
    public void DecryptXorPassword_EmptyStored_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DockerChallengeImageBuilder.DecryptXorPassword(null, [1, 2, 3]));
        Assert.Equal(string.Empty, DockerChallengeImageBuilder.DecryptXorPassword("", [1, 2, 3]));
    }

    [Fact]
    public void DecryptXorPassword_MalformedBase64_FallsThroughToStored()
    {
        // Pre-encryption legacy value or just a misconfigured XorKey —
        // we don't want a corrupt PAT to permanently break pushes.
        // Returning the stored value at least keeps the system functional.
        const string notBase64 = "not!valid!base64";
        Assert.Equal(notBase64, DockerChallengeImageBuilder.DecryptXorPassword(notBase64, [1, 2, 3]));
    }

    #endregion
}
