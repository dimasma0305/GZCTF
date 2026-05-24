using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace GZCTF.Utils;

/// <summary>
/// Token primitives for the per-team A&amp;D API token. Kept as static helpers
/// so the controller stays thin and so unit tests can exercise the format
/// contract without spinning up a controller.
/// </summary>
public static class AdTokenUtils
{
    /// <summary>Visible prefix on every plaintext token (helps users recognize them in logs).</summary>
    public const string TokenPrefix = "ad_";

    /// <summary>32 random bytes → 43 base64url chars; prefixed with <c>ad_</c>.</summary>
    public static string GeneratePlaintext()
    {
        var raw = RandomNumberGenerator.GetBytes(32);
        return TokenPrefix + WebEncoders.Base64UrlEncode(raw);
    }

    /// <summary>
    /// Short public hint shown in the UI (e.g. <c>ad_a1b2…f9e8</c>). Reveals
    /// 8 chars total so two distinct tokens are extremely unlikely to collide
    /// in the same hint without also being similar enough that the captain
    /// notices.
    /// </summary>
    public static string BuildHint(string plaintext)
    {
        if (plaintext.Length < 12)
            return plaintext;

        return $"{plaintext[..7]}…{plaintext[^4..]}";
    }

    /// <summary>
    /// HMAC-SHA256 of the plaintext, keyed with the application's XOR key.
    /// One-way: a DB leak does not yield usable tokens; rotation
    /// unconditionally invalidates the previous token (different hash).
    /// </summary>
    public static byte[] Hash(string plaintext, byte[] key)
    {
        if (key.Length == 0)
            key = "ad-default-key-not-secure"u8.ToArray();

        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(plaintext));
    }
}
