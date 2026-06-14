using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
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

    #region WriteContextTarAsync — content-hash determinism

    // Lay down a throwaway build context with the given relative files.
    private static string MakeContext(params (string rel, string content)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gzctf-test-" + Guid.NewGuid().ToString("N"));
        foreach (var (rel, content) in files)
        {
            var full = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return dir;
    }

    private static async Task<string> TarDigest(string dir)
    {
        var outPath = Path.Combine(Path.GetTempPath(), "gzctf-test-" + Guid.NewGuid().ToString("N") + ".tar.gz");
        try
        {
            return await DockerChallengeImageBuilder.WriteContextTarAsync(dir, outPath, default);
        }
        finally
        {
            try { File.Delete(outPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task WriteContextTar_SameContent_StableAcrossMtimesAndReruns()
    {
        // This is the regression guard for "multiple images per challenge": a fresh
        // PaxTarEntry stamps DateTimeOffset.UtcNow as mtime, so before the fix the same
        // content hashed differently on every build. The digest must ignore file mtimes.
        var dir = MakeContext(
            ("Dockerfile", "FROM scratch\n"),
            ("src/app.py", "print('hi')\n"),
            ("README.md", "# challenge\n"));
        try
        {
            var first = await TarDigest(dir);

            // Bump every file's mtime — the prime drift source — then re-tar.
            var future = DateTime.UtcNow.AddDays(3);
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetLastWriteTimeUtc(f, future);

            var second = await TarDigest(dir);

            Assert.Equal(first, second);
            Assert.Equal(64, first.Length); // SHA-256 as lowercase hex
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task WriteContextTar_SameContent_DifferentDirsAndCreateOrder_SameDigest()
    {
        // Two independent contexts with identical {path,content} but files created in a
        // different order must hash the same — proves the ordinal-path sort makes entry
        // order independent of filesystem enumeration order.
        var a = MakeContext(("Dockerfile", "FROM scratch\n"), ("a/b/c.txt", "data\n"));
        var b = MakeContext(("a/b/c.txt", "data\n"), ("Dockerfile", "FROM scratch\n"));
        try
        {
            Assert.Equal(await TarDigest(a), await TarDigest(b));
        }
        finally { Directory.Delete(a, true); Directory.Delete(b, true); }
    }

    [Fact]
    public async Task WriteContextTar_DifferentContent_DifferentDigest()
    {
        var a = MakeContext(("Dockerfile", "FROM scratch\n"));
        var b = MakeContext(("Dockerfile", "FROM alpine\n"));
        try
        {
            Assert.NotEqual(await TarDigest(a), await TarDigest(b));
        }
        finally { Directory.Delete(a, true); Directory.Delete(b, true); }
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
