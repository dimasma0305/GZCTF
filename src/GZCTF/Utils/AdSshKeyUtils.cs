using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace GZCTF.Utils;

/// <summary>
/// SSH key primitives for A&amp;D shell access. Two flows:
/// <list type="bullet">
///   <item><b>Player-uploads</b>: validate an OpenSSH-format public key
///         (<c>ssh-ed25519 AAAA... [comment]</c>), extract the algorithm +
///         raw blob, compute its SHA256 fingerprint (the same format
///         <c>ssh-keygen -lf</c> prints — <c>SHA256:&lt;base64&gt;</c>).</item>
///   <item><b>Platform-generates</b>: produce a fresh ed25519 keypair the
///         user downloads once, returned in OpenSSH on-disk format (so the
///         user can drop it into <c>~/.ssh/</c> and use it with stock
///         <c>ssh</c> / <c>ssh-agent</c>).</item>
/// </list>
/// </summary>
public static class AdSshKeyUtils
{
    public sealed record ParsedKey(string Algorithm, byte[] Blob, string Fingerprint, string? Comment);

    public sealed record GeneratedKeyPair(string PublicKeyOpenSsh, string PrivateKeyOpenSsh, string Fingerprint);

    private static readonly HashSet<string> AllowedAlgorithms = new(StringComparer.Ordinal)
    {
        "ssh-ed25519",
        "ssh-rsa",
        "ecdsa-sha2-nistp256",
        "ecdsa-sha2-nistp384",
        "ecdsa-sha2-nistp521"
    };

    /// <summary>
    /// Parse a single-line OpenSSH public key. Returns the algorithm, the
    /// raw key blob, and the fingerprint. Throws <see cref="FormatException"/>
    /// for malformed input. We refuse algorithms outside
    /// <see cref="AllowedAlgorithms"/> so attackers can't smuggle a custom
    /// key type the server-side parsing doesn't validate.
    /// </summary>
    public static ParsedKey Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new FormatException("Empty public key");

        // Strip authorized_keys options if present (anything before the
        // algorithm word). We only accept the simple `algo base64 [comment]`
        // shape — no `command="…"` / `from="…"` directives.
        var trimmed = input.Trim();
        var parts = trimmed.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new FormatException("Public key must be 'algorithm base64 [comment]'");

        var algorithm = parts[0];
        if (!AllowedAlgorithms.Contains(algorithm))
            throw new FormatException($"Unsupported key algorithm: {algorithm}");

        byte[] blob;
        try { blob = Convert.FromBase64String(parts[1]); }
        catch (FormatException) { throw new FormatException("Public key blob is not valid base64"); }

        if (blob.Length is < 32 or > 4096)
            throw new FormatException("Public key blob has implausible length");

        // The blob starts with a length-prefixed algorithm name that must
        // match the prefix word — otherwise it's a malformed/spoofed key.
        var embedded = ReadSshString(blob, 0, out _);
        if (embedded is null || !string.Equals(embedded, algorithm, StringComparison.Ordinal))
            throw new FormatException("Public key blob's embedded algorithm doesn't match its prefix");

        string? comment = parts.Length > 2 ? parts[2].Trim() : null;
        if (comment is not null && comment.Length > 256)
            comment = comment[..256];

        return new ParsedKey(algorithm, blob, Fingerprint(blob), comment);
    }

    /// <summary>
    /// SHA256 fingerprint in the OpenSSH-canonical
    /// <c>SHA256:&lt;unpadded-base64&gt;</c> shape — matches
    /// <c>ssh-keygen -lf</c> output so the user can verify their upload
    /// against their local <c>~/.ssh</c>.
    /// </summary>
    public static string Fingerprint(byte[] blob)
    {
        var hash = SHA256.HashData(blob);
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }

    /// <summary>
    /// Generate a fresh ed25519 keypair via BouncyCastle. Returns both
    /// halves in OpenSSH on-disk formats — public key as a single-line
    /// <c>ssh-ed25519 AAAA…</c>, private key as a PEM-wrapped
    /// <c>OPENSSH PRIVATE KEY</c> block. No passphrase (we already gate
    /// the download behind cookie auth).
    /// </summary>
    public static GeneratedKeyPair GenerateEd25519(string comment)
    {
        var rng = new SecureRandom();
        var gen = new Ed25519KeyPairGenerator();
        gen.Init(new Ed25519KeyGenerationParameters(rng));
        var pair = gen.GenerateKeyPair();
        var pub = ((Ed25519PublicKeyParameters)pair.Public).GetEncoded();
        var priv = ((Ed25519PrivateKeyParameters)pair.Private).GetEncoded();

        var blob = EncodeEd25519PublicBlob(pub);
        var pubLine = $"ssh-ed25519 {Convert.ToBase64String(blob)} {comment}".TrimEnd();
        var privBlock = EncodeOpenSshEd25519Private(pub, priv, comment, rng);
        return new GeneratedKeyPair(pubLine, privBlock, Fingerprint(blob));
    }

    /// <summary>
    /// SSH wire format: 4-byte BE length + bytes. Returns the string at
    /// <paramref name="offset"/> and advances <paramref name="next"/>, or
    /// null if the buffer is too short.
    /// </summary>
    private static string? ReadSshString(byte[] buf, int offset, out int next)
    {
        next = offset;
        if (buf.Length < offset + 4) return null;
        int len = (buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3];
        if (len < 0 || len > buf.Length - offset - 4) return null;
        next = offset + 4 + len;
        return Encoding.ASCII.GetString(buf, offset + 4, len);
    }

    private static byte[] EncodeEd25519PublicBlob(byte[] pub32)
    {
        // SSH wire format: <ssh-ed25519><pub-bytes>
        using var ms = new MemoryStream();
        WriteSshString(ms, "ssh-ed25519"u8);
        WriteSshBytes(ms, pub32);
        return ms.ToArray();
    }

    private static string EncodeOpenSshEd25519Private(byte[] pub, byte[] priv, string comment, SecureRandom rng)
    {
        // OpenSSH private key format — see PROTOCOL.key in openssh-portable.
        // Layout: "openssh-key-v1\0" + ciphername + kdfname + kdfopts +
        //         num_keys=1 + pubkey blob + private section.
        // Private section: check1 + check2 (== for integrity check) + key
        // entries + comment + padding to cipher block size (8).
        using var body = new MemoryStream();
        body.Write("openssh-key-v1\0"u8);
        WriteSshString(body, "none"u8);   // ciphername
        WriteSshString(body, "none"u8);   // kdfname
        WriteSshBytes(body, []);           // kdfopts (empty)
        WriteUint32(body, 1);              // number of keys

        var pubBlob = EncodeEd25519PublicBlob(pub);
        WriteSshBytes(body, pubBlob);

        using var priv1 = new MemoryStream();
        var check = new byte[4];
        rng.NextBytes(check);
        priv1.Write(check); priv1.Write(check); // check1 == check2
        WriteSshString(priv1, "ssh-ed25519"u8);
        WriteSshBytes(priv1, pub);
        // The "private key" in OpenSSH ed25519 layout is seed || pub (64 bytes total)
        var seedPlusPub = new byte[priv.Length + pub.Length];
        Buffer.BlockCopy(priv, 0, seedPlusPub, 0, priv.Length);
        Buffer.BlockCopy(pub, 0, seedPlusPub, priv.Length, pub.Length);
        WriteSshBytes(priv1, seedPlusPub);
        WriteSshString(priv1, Encoding.UTF8.GetBytes(comment));

        // Pad to 8-byte boundary with 1,2,3,…
        var privArr = priv1.ToArray();
        var pad = (8 - (privArr.Length % 8)) % 8;
        var padded = new byte[privArr.Length + pad];
        Buffer.BlockCopy(privArr, 0, padded, 0, privArr.Length);
        for (int i = 0; i < pad; i++) padded[privArr.Length + i] = (byte)(i + 1);
        WriteSshBytes(body, padded);

        var b64 = Convert.ToBase64String(body.ToArray());
        var sb = new StringBuilder();
        sb.Append("-----BEGIN OPENSSH PRIVATE KEY-----\n");
        for (int i = 0; i < b64.Length; i += 70)
            sb.Append(b64.AsSpan(i, Math.Min(70, b64.Length - i))).Append('\n');
        sb.Append("-----END OPENSSH PRIVATE KEY-----\n");
        return sb.ToString();
    }

    private static void WriteUint32(Stream s, uint v)
    {
        s.WriteByte((byte)((v >> 24) & 0xff));
        s.WriteByte((byte)((v >> 16) & 0xff));
        s.WriteByte((byte)((v >> 8) & 0xff));
        s.WriteByte((byte)(v & 0xff));
    }

    private static void WriteSshBytes(Stream s, byte[] bytes)
    {
        WriteUint32(s, (uint)bytes.Length);
        s.Write(bytes);
    }

    private static void WriteSshString(Stream s, ReadOnlySpan<byte> ascii)
    {
        WriteUint32(s, (uint)ascii.Length);
        s.Write(ascii);
    }

    /// <summary>
    /// XOR-wrap a freshly-generated private key for at-rest storage —
    /// same primitive as <see cref="AdVpnKeys.WrapPrivateKey"/>. Empty
    /// XorKey leaves bytes plain (the platform refuses to start without
    /// a key in production, but dev configs may omit it).
    /// </summary>
    public static string WrapPrivateKey(string pem, byte[] xorKey)
    {
        var bytes = Encoding.UTF8.GetBytes(pem);
        return Convert.ToBase64String(xorKey.Length == 0 ? bytes : Codec.Xor(bytes, xorKey));
    }
}
