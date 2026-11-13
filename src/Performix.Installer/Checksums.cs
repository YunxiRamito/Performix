using System.Security.Cryptography;

namespace Performix.Installer;

public enum HashKind
{
    Unknown,
    Sha256,
    Sha512,
}

/// <summary>A published digest: which algorithm and the raw bytes.</summary>
public sealed record ExpectedHash(HashKind Kind, byte[] Bytes)
{
    /// <summary>Length of the digest for this algorithm.</summary>
    public int Length => Bytes.Length;
}

/// <summary>
/// Digest parsing and comparison.
///
/// The two sources disagree about encoding and that is the whole reason this type exists:
/// releases.json publishes SHA-512 as 128 lowercase hex characters (verified against
/// builds.dotnet.microsoft.com, 10.0/releases.json), while GitHub release asset <c>digest</c>
/// fields and <c>.sha256</c> files use SHA-256 hex. Base64 is also accepted because several
/// other Microsoft feeds use it for the same field.
/// </summary>
public static class Checksums
{
    /// <summary>
    /// Parses <c>"f838…"</c> (128 hex → SHA-512), <c>"9f2c…"</c> (64 hex → SHA-256), a base64
    /// SHA-512 (88 chars) or a base64 SHA-256 (44 chars), optionally prefixed with the algorithm
    /// name (<c>sha512:…</c> / <c>sha256:…</c>).
    /// </summary>
    public static bool TryParse(string? text, out ExpectedHash? hash)
    {
        hash = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string value = text.Trim();

        // "sha512:ABC..." / "sha256:ABC..." (the GitHub asset digest form).
        int colon = value.IndexOf(':');
        if (colon > 0 && colon < 16)
        {
            string prefix = value[..colon].ToLowerInvariant();
            string rest = value[(colon + 1)..].Trim();
            if (prefix is "sha512" or "sha-512")
            {
                return TryParseBytes(rest, HashKind.Sha512, out hash);
            }

            if (prefix is "sha256" or "sha-256")
            {
                return TryParseBytes(rest, HashKind.Sha256, out hash);
            }
        }

        if (IsHex(value))
        {
            switch (value.Length)
            {
                case 128:
                    hash = new ExpectedHash(HashKind.Sha512, Convert.FromHexString(value));
                    return true;
                case 64:
                    hash = new ExpectedHash(HashKind.Sha256, Convert.FromHexString(value));
                    return true;
                default:
                    return false;
            }
        }

        if (value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='))
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                return false;
            }

            switch (bytes.Length)
            {
                case 64:
                    hash = new ExpectedHash(HashKind.Sha512, bytes);
                    return true;
                case 32:
                    hash = new ExpectedHash(HashKind.Sha256, bytes);
                    return true;
                default:
                    return false;
            }
        }

        return false;
    }

    /// <summary>Parses a bare digest string that is already known to be <paramref name="kind"/>.</summary>
    private static bool TryParseBytes(string value, HashKind kind, out ExpectedHash? hash)
    {
        hash = null;
        if (IsHex(value) && value.Length == (kind == HashKind.Sha512 ? 128 : 64))
        {
            hash = new ExpectedHash(kind, Convert.FromHexString(value));
            return true;
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(value);
            if (bytes.Length == (kind == HashKind.Sha512 ? 64 : 32))
            {
                hash = new ExpectedHash(kind, bytes);
                return true;
            }
        }
        catch (FormatException)
        {
            return false;
        }

        return false;
    }

    public static bool IsHex(string? value)
        => !string.IsNullOrEmpty(value) && value.All(char.IsAsciiHexDigit);

    /// <summary>Hash of a file on disk, using the algorithm the hash claims.</summary>
    public static byte[] HashFile(string path, HashKind kind)
    {
        using FileStream stream = File.OpenRead(path);
        return kind == HashKind.Sha512 ? SHA512.HashData(stream) : SHA256.HashData(stream);
    }

    public static byte[] HashBytes(ReadOnlySpan<byte> bytes, HashKind kind)
        => kind == HashKind.Sha512 ? SHA512.HashData(bytes) : SHA256.HashData(bytes);

    /// <summary>Constant-time-ish comparison of a computed digest against the expected one.</summary>
    public static bool Matches(ReadOnlySpan<byte> actual, ExpectedHash? expected)
        => expected is not null
           && actual.Length == expected.Bytes.Length
           && CryptographicOperations.FixedTimeEquals(actual, expected.Bytes);

    public static string ToHex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>Human-readable algorithm name for log lines.</summary>
    public static string Describe(HashKind kind) => kind switch
    {
        HashKind.Sha512 => "SHA-512",
        HashKind.Sha256 => "SHA-256",
        _ => "unknown hash",
    };
}
