using System.Security.Cryptography;
using System.Text;

namespace Cleanse.Core.Domain;

/// <summary>
/// Builds stable identifiers from content rather than from a clock or a counter. An import that
/// runs twice produces the same ids, so the second run upserts instead of duplicating.
/// </summary>
public static class DeterministicId
{
    /// <summary>
    /// RFC 9562 version 5: SHA-1 of a namespace plus a name, with the version and variant bits
    /// overwritten. Chosen over a raw hash so the value is a well-formed UUID that any database
    /// will store and index natively.
    /// </summary>
    public static Guid Create(Guid @namespace, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var namespaceBytes = @namespace.ToByteArray();
        ToBigEndian(namespaceBytes);

        var nameBytes = Encoding.UTF8.GetBytes(name);
        var buffer = new byte[namespaceBytes.Length + nameBytes.Length];
        namespaceBytes.CopyTo(buffer, 0);
        nameBytes.CopyTo(buffer, namespaceBytes.Length);

        var hash = SHA1.HashData(buffer);

        var result = new byte[16];
        Array.Copy(hash, result, 16);

        result[6] = (byte)((result[6] & 0x0F) | 0x50);
        result[8] = (byte)((result[8] & 0x3F) | 0x80);

        ToBigEndian(result);
        return new Guid(result);
    }

    /// <summary>Namespace for history entries mirrored from an external system.</summary>
    public static readonly Guid ExternalHistoryNamespace =
        new("8f1d2c3a-5b47-4e9a-9c21-0f6b7d8e4a15");

    /// <summary>
    /// One entry per issue, source, column and timestamp. Re-importing the same extract yields
    /// the same id, so a third "1st contact" row never appears.
    /// </summary>
    public static Guid ForExternalHistory(IssueId issueId, string source, string column, DateTimeOffset at) =>
        Create(
            ExternalHistoryNamespace,
            $"{issueId.Value}|{source}|{column}|{at.ToUniversalTime():O}");

    private static void ToBigEndian(byte[] guidBytes)
    {
        if (!BitConverter.IsLittleEndian)
        {
            return;
        }

        (guidBytes[0], guidBytes[3]) = (guidBytes[3], guidBytes[0]);
        (guidBytes[1], guidBytes[2]) = (guidBytes[2], guidBytes[1]);
        (guidBytes[4], guidBytes[5]) = (guidBytes[5], guidBytes[4]);
        (guidBytes[6], guidBytes[7]) = (guidBytes[7], guidBytes[6]);
    }
}
