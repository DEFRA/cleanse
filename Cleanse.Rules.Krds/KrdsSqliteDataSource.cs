using System.Runtime.CompilerServices;
using Cleanse.Rules.Abstractions;
using Cleanse.Rules.Domain;
using Cleanse.Rules.Krds.Snapshots;
using Microsoft.Data.Sqlite;

namespace Cleanse.Rules.Krds;

/// <summary>
/// Reads CTS open locations and their SAM holdings from a KRDS SQLite snapshot.
/// </summary>
/// <remarks>
/// One query does the whole primary pass. The SAM holding is a left join so that its absence —
/// which is what rule 2 detects — arrives as a null rather than a missing row, and the species
/// codes come from a correlated subquery rather than a second round trip per record, so the
/// pass costs one scan instead of one query per holding.
/// </remarks>
internal sealed class KrdsSqliteDataSource : ICtsSamDataSource
{
    /// <summary>
    /// Restricts the pass to real CPHs. CTS files slaughterhouses in the same dataset under a
    /// bare number, so this pattern is also the slaughterhouse exclusion.
    /// </summary>
    internal const string CphPattern = "[0-9][0-9]/[0-9][0-9][0-9]/[0-9][0-9][0-9][0-9]";

    private const string CountSql = $"""
        SELECT COUNT(*) FROM CtsOpenLocation WHERE Cph GLOB '{CphPattern}'
        """;

    private const string StreamSql = $"""
        SELECT c.LocationNumber,
               c.Cph,
               c.PremisesType,
               c.CountyName,
               c.KeeperSurname,
               c.KeeperPostCode,
               h.Id,
               h.FeatureName,
               (SELECT GROUP_CONCAT(DISTINCT p.AnimalSpeciesCode)
                  FROM HoldingAnimalProfile p
                 WHERE p.HoldingId = h.Id)
          FROM CtsOpenLocation c
          LEFT JOIN Holding h ON h.Cph = c.Cph
         WHERE c.Cph GLOB '{CphPattern}'
         ORDER BY c.LocationNumber
        """;

    private readonly KrdsSnapshot _snapshot;
    private readonly string _connectionString;

    public KrdsSqliteDataSource(KrdsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = snapshot.FilePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task<int> CountRecordsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = CountSql;

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value ?? 0);
    }

    public async IAsyncEnumerable<CtsSamRecord> StreamRecordsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = StreamSql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var raw = reader.GetString(1);
            if (!Cph.TryParse(raw, out var cph))
            {
                // The GLOB already excluded these; belt and braces against a schema change.
                continue;
            }

            yield return new CtsSamRecord
            {
                CtsLocationNumber = reader.GetString(0),
                Cph = cph,
                CtsPremisesType = Text(reader, 2),
                CtsCountyName = Text(reader, 3),
                CtsKeeperSurname = Text(reader, 4),
                CtsKeeperPostCode = Text(reader, 5),
                SamHoldingId = Text(reader, 6),
                SamFeatureName = Text(reader, 7),
                SamSpeciesCodes = SpeciesCodes(Text(reader, 8))
            };
        }
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        return _snapshot.DisposeAsync();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>
    /// Trims, and strips the literal quote characters the upstream export wraps some values in.
    /// </summary>
    private static string? Text(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetString(ordinal).Trim().Trim('\'');
        return value.Length == 0 ? null : value;
    }

    private static IReadOnlySet<string> SpeciesCodes(string? concatenated)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(concatenated))
        {
            return codes;
        }

        foreach (var code in concatenated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            codes.Add(code);
        }

        return codes;
    }
}
