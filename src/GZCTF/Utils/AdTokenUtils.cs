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

    /// <summary>
    /// Stable per-(participation, challenge) token for the Kubernetes flag-pull
    /// endpoint (<c>GET …/Ad/PodFlag/{pid}/{cid}/{token}</c>). HMAC of the pair
    /// keyed by the XorKey, hex-encoded — unguessable and scoped to that team's
    /// own flag. Computed at pod launch (injected into the flag-writer sidecar's
    /// URL) and re-derived + compared by the endpoint.
    /// </summary>
    public static string PodFlagToken(int participationId, int challengeId, byte[] key) =>
        Convert.ToHexString(Hash($"adpodflag:{participationId}:{challengeId}", key));

    /// <summary>
    /// Stable per-(participation, challenge) token for the BYOC (self-hosted)
    /// agent tunnel endpoint (<c>GET …/Ad/Byoc/Agent/{pid}/{cid}/{token}</c>).
    /// HMAC of the pair keyed by the XorKey, hex-encoded — unguessable and scoped
    /// to that team's own relay. Baked into the team's generated agent config; the
    /// endpoint re-derives and compares it. A distinct domain prefix from
    /// <see cref="PodFlagToken"/> means neither token is usable as the other.
    /// </summary>
    public static string ByocAgentToken(int participationId, int challengeId, byte[] key) =>
        Convert.ToHexString(Hash($"adbyocagent:{participationId}:{challengeId}", key));

    /// <summary>
    /// Per-(participation, challenge) secret authenticating GZCTF to a BYOC relay's
    /// control + flag ports. Those ports live on the shared challenge bridge, which
    /// jeopardy containers (a compromised solve target) also sit on, so network
    /// position alone is NOT sufficient: GZCTF presents this secret on every relay
    /// connection and the relay rejects any peer that doesn't. Injected into the
    /// relay at launch (env <c>GZCTF_BYOC_SECRET</c>) and re-derived by the agent
    /// WS bridge + the per-tick flag push. NEVER given to the team (gzctf↔relay only).
    /// </summary>
    public static string ByocRelaySecret(int participationId, int challengeId, byte[] key) =>
        Convert.ToHexString(Hash($"adbyocrelay:{participationId}:{challengeId}", key));
}
