using System.Security.Cryptography;
using System.Text;

namespace Cleanse.Core.Domain;

/// <summary>
/// The natural key of an issue. Always supplied by the caller so that a rule re-detecting the
/// same anomaly on a later night produces the same identity and therefore upserts rather than
/// inserts. <see cref="Derive"/> is a convenience for building stable composite keys; Core does
/// not impose a scheme.
/// </summary>
public readonly record struct IssueId
{
    /// <summary>
    /// Unreserved, so that percent-encoding a derived id is a no-op. ':' would have been more
    /// readable but Uri.EscapeDataString and encodeURIComponent both escape it.
    /// </summary>
    private const char RuleSeparator = '-';

    /// <summary>Digest bytes kept. 80 bits encodes to exactly 16 base32 characters, no padding.</summary>
    private const int DigestBytes = 10;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private readonly string? _value;

    private IssueId(string value) => _value = value;

    public string Value => _value ?? throw new InvalidOperationException("IssueId has not been initialised.");

    public bool HasValue => !string.IsNullOrEmpty(_value);

    public static IssueId From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new IssueId(value.Trim());
    }

    /// <summary>
    /// Builds a URL-safe composite key of the form <c>ruleId:digest</c>, where the digest is a
    /// base32 SHA-256 over the length-prefixed parts.
    /// </summary>
    /// <remarks>
    /// The parts are hashed rather than concatenated because they routinely contain characters
    /// that are illegal in a URL path — the slashes in a CPH above all — and an id that has to be
    /// escaped to be addressable leaks encoding bugs into every consumer. Length-prefixing each
    /// part is what keeps the digest collision-free: without it <c>["ab", "c"]</c> and
    /// <c>["a", "bc"]</c> would hash identically.
    /// <para>
    /// The inputs are deliberately not recoverable from the id. Anything a human needs to read is
    /// carried on the issue's context, which is where a consumer should look.
    /// </para>
    /// </remarks>
    public static IssueId Derive(string ruleId, params string[] parts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Length == 0)
        {
            throw new ArgumentException("At least one discriminating part is required.", nameof(parts));
        }

        var rule = ruleId.Trim();
        if (!IsUrlSafe(rule))
        {
            throw new ArgumentException(
                $"Rule id '{rule}' must contain only unreserved URL characters.", nameof(ruleId));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendSegment(hash, rule);
        foreach (var part in parts)
        {
            AppendSegment(hash, part ?? string.Empty);
        }

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        hash.GetHashAndReset(digest);

        return new IssueId($"{rule}{RuleSeparator}{Base32(digest[..DigestBytes])}");
    }

    /// <summary>
    /// Whether the value can sit in a URL without escaping: the RFC 3986 unreserved characters,
    /// and nothing else.
    /// </summary>
    public static bool IsUrlSafe(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '.' or '_' or '~'))
            {
                return false;
            }
        }

        return true;
    }

    private static void AppendSegment(IncrementalHash hash, string segment)
    {
        var bytes = Encoding.UTF8.GetBytes(segment);

        Span<byte> length = stackalloc byte[sizeof(int)];
        BitConverter.TryWriteBytes(length, bytes.Length);

        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static string Base32(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length * 8 / 5];
        var buffer = 0;
        var bits = 0;
        var index = 0;

        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;

            while (bits >= 5)
            {
                bits -= 5;
                chars[index++] = Base32Alphabet[(buffer >> bits) & 31];
            }
        }

        return new string(chars);
    }

    public override string ToString() => _value ?? string.Empty;

    public static implicit operator string(IssueId id) => id.Value;
}
