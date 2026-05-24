using System.Net;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace GZCTF.Utils;

/// <summary>
/// WireGuard key + IP primitives for A&amp;D VPN provisioning. Pure helpers so
/// the controller stays thin and tests can exercise the format/correctness
/// without a database or HTTP context.
/// </summary>
public static class AdVpnKeys
{
    /// <summary>
    /// Generate a fresh X25519 keypair via BouncyCastle. Returns raw 32-byte
    /// public + private; WireGuard configs use base64 of these bytes.
    /// </summary>
    public static (byte[] PublicKey, byte[] PrivateKey) GenerateX25519KeyPair()
    {
        var rng = new SecureRandom();
        var gen = new X25519KeyPairGenerator();
        gen.Init(new X25519KeyGenerationParameters(rng));
        var pair = gen.GenerateKeyPair();
        var priv = ((X25519PrivateKeyParameters)pair.Private).GetEncoded();
        var pub = ((X25519PublicKeyParameters)pair.Public).GetEncoded();
        return (pub, priv);
    }

    /// <summary>
    /// Assign the next available IPv4 from a CIDR (e.g. <c>10.13.37.0/24</c>),
    /// skipping <c>.0</c> (network), <c>.1</c> (reserved for WG server), and
    /// any IP in <paramref name="usedIps"/>. Throws when the subnet is exhausted.
    /// </summary>
    public static string AssignNextIp(string cidr, IEnumerable<string> usedIps)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network) ||
            !int.TryParse(parts[1], out var prefix))
            throw new InvalidOperationException($"Invalid VPN client CIDR: {cidr}");

        if (network.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidOperationException("Only IPv4 CIDRs are supported for now");

        var used = new HashSet<string>(usedIps);
        var addrBytes = network.GetAddressBytes();
        int hostBits = 32 - prefix;
        if (hostBits < 2)
            throw new InvalidOperationException("VPN client CIDR must have at least /30");

        long hostCount = 1L << hostBits;
        // i=2 skips network (.0) and the WG server (.1). Stop one short of broadcast.
        for (long i = 2; i < hostCount - 1; i++)
        {
            var b = (byte[])addrBytes.Clone();
            // Walk the host portion of the address.
            long v = i;
            for (int j = 3; j >= 0 && v > 0; j--)
            {
                b[j] = (byte)((b[j] | (v & 0xff)) & 0xff);
                v >>= 8;
            }
            var candidate = new IPAddress(b).ToString();
            if (!used.Contains(candidate)) return candidate;
        }

        throw new InvalidOperationException("VPN client subnet exhausted");
    }

    /// <summary>
    /// Encrypt a raw WireGuard private key for at-rest storage using the same
    /// XOR pattern as repo PATs / SSH keys. Returns base64.
    /// </summary>
    public static string WrapPrivateKey(byte[] raw, byte[] xorKey)
    {
        if (xorKey.Length == 0)
            return Convert.ToBase64String(raw);
        return Convert.ToBase64String(Codec.Xor(raw, xorKey));
    }

    /// <summary>
    /// Inverse of <see cref="WrapPrivateKey"/>; returns base64 of the raw key
    /// (the shape WG configs expect).
    /// </summary>
    public static string UnwrapPrivateKey(string stored, byte[] xorKey)
    {
        var bytes = Convert.FromBase64String(stored);
        if (xorKey.Length == 0)
            return Convert.ToBase64String(bytes);
        return Convert.ToBase64String(Codec.Xor(bytes, xorKey));
    }
}
