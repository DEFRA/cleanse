using System.Data;
using System.Globalization;
using System.Text.Json;
using Cleanse.Core.Domain;
using Microsoft.Data.Sqlite;

namespace Cleanse.Storage.Sqlite.Schema;

/// <summary>
/// Row translation. Deliberately parallel to the Postgres provider's mapper rather than shared
/// with it: the two store different physical types for the same domain value, and a common
/// helper would have to know about both.
/// </summary>
internal static class SqliteMappings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Round-trip format. Fixed width and offset-invariant for the UTC values this service
    /// writes, which is what lets SQLite order and range-filter timestamps as plain text.
    /// </summary>
    private const string TimestampFormat = "O";

    public static string ToText(this DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    public static DateTimeOffset ToTimestamp(string value) =>
        DateTimeOffset.ParseExact(value, TimestampFormat, CultureInfo.InvariantCulture);

    public static string SerialiseContext(IssueContext context) =>
        context.Count == 0
            ? "{}"
            : JsonSerializer.Serialize(
                context.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal), Json);

    public static IssueContext DeserialiseContext(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? IssueContext.Empty
            : IssueContext.From(JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json));

    public static string SerialiseChanges(IReadOnlyList<AuditChange> changes) =>
        JsonSerializer.Serialize(changes, Json);

    public static IReadOnlyList<AuditChange> DeserialiseChanges(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<AuditChange>>(json, Json) ?? [];

    public static Actor RebuildActor(string id, string displayName, string kind) =>
        Enum.Parse<ActorKind>(kind, ignoreCase: true) == ActorKind.System
            ? id == Actor.SystemId ? Actor.System : Actor.Machine(id, displayName)
            : Actor.Human(id, displayName);

    public static IssueSnapshot ReadIssue(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        ParseIssueStatus(reader.GetString(4)),
        ParseWorkflowStatus(reader.GetString(5)),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : ParsePriority(reader.GetString(9)),
        reader.IsDBNull(10) ? null : ToTimestamp(reader.GetString(10)),
        reader.IsDBNull(11) ? null : ToTimestamp(reader.GetString(11)),
        DeserialiseContext(reader.GetString(12)),
        ToTimestamp(reader.GetString(13)),
        ToTimestamp(reader.GetString(14)),
        ToTimestamp(reader.GetString(15)),
        reader.IsDBNull(16) ? null : ToTimestamp(reader.GetString(16)),
        reader.GetInt32(17));

    public static IssuePriority ParsePriority(string value) =>
        StatusNames.TryParsePriority(value, out var priority)
            ? priority
            : throw new InvalidOperationException($"'{value}' is not a known priority.");

    public static IssueStatus ParseIssueStatus(string value) =>
        StatusNames.TryParseIssueStatus(value, out var status)
            ? status
            : throw new InvalidOperationException($"'{value}' is not a known issue status.");

    public static WorkflowStatus ParseWorkflowStatus(string value) =>
        StatusNames.TryParseWorkflowStatus(value, out var status)
            ? status
            : throw new InvalidOperationException($"'{value}' is not a known workflow status.");

    public static void Bind(this SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    public static async Task<int> ExecuteAsync(
        this SqliteCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Connection!.State != ConnectionState.Open)
        {
            await command.Connection.OpenAsync(cancellationToken);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
