using System.Globalization;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using CsvHelper;
using CsvHelper.Configuration;

namespace Cleanse.Api.Imports;

/// <summary>
/// Reads the SharePoint list export. Everything is taken literally from the columns: no meaning
/// is inferred from the free-text notes, so the import never asserts more than the source says.
/// </summary>
public sealed class SpolCsvReader
{
    public const string Source = "spol";

    private static readonly CsvConfiguration Configuration = new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = true,
        TrimOptions = TrimOptions.Trim,
        MissingFieldFound = null,
        BadDataFound = null
    };

    /// <summary>Columns the extract must carry, checked before a byte is persisted.</summary>
    public static readonly string[] RequiredColumns =
        ["CPH", "Rule No", "Status", "Notes", "Priority", "Date Received"];

    private static readonly (string Column, HistoryActionType Action)[] ContactColumns =
    [
        ("1st Contact Date", HistoryActionType.PhoneCall),
        ("2nd Contact Date", HistoryActionType.PhoneCall),
        ("3rd Contact Date", HistoryActionType.PhoneCall),
        ("Letter/email sent", HistoryActionType.LetterSent)
    ];

    public static bool TryValidateHeader(Stream content, out string? error)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var reader = new StreamReader(content, leaveOpen: true);
        using var csv = new CsvReader(reader, Configuration);

        if (!csv.Read() || !csv.ReadHeader())
        {
            error = "The file has no header row.";
            return false;
        }

        var header = csv.HeaderRecord ?? [];
        var missing = RequiredColumns
            .Where(c => !header.Contains(c, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (missing.Length > 0)
        {
            error = $"The file is missing required columns: {string.Join(", ", missing)}.";
            return false;
        }

        error = null;
        return true;
    }

    public static IEnumerable<SpolRow> Read(Stream content, string groupId)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        using var reader = new StreamReader(content);
        using var csv = new CsvReader(reader, Configuration);

        csv.Read();
        csv.ReadHeader();

        long rowNumber = 1;
        while (csv.Read())
        {
            rowNumber++;
            yield return ReadRow(csv, groupId, rowNumber);
        }
    }

    private static SpolRow ReadRow(IReaderRow csv, string groupId, long rowNumber)
    {
        var cph = Field(csv, "CPH");
        var ruleNo = Field(csv, "Rule No");

        var contacts = new List<ExternalContactEvent>();
        foreach (var (column, action) in ContactColumns)
        {
            if (TryDate(Field(csv, column), out var at))
            {
                contacts.Add(new ExternalContactEvent(column, action, at));
            }
        }

        return new SpolRow
        {
            RowNumber = rowNumber,
            GroupId = groupId,
            Cph = cph,
            RuleNo = ruleNo,
            Status = Field(csv, "Status"),
            Notes = Field(csv, "Notes"),
            HasAssignee = csv.HeaderRecord?.Contains("Assigned to", StringComparer.OrdinalIgnoreCase) == true,
            AssignedTo = Field(csv, "Assigned to"),
            Priority = Field(csv, "Priority"),
            DateReceived = TryDate(Field(csv, "Date Received"), out var received) ? received : null,
            DateComplete = TryDate(Field(csv, "Date Complete"), out var complete) ? complete : null,
            Contacts = contacts
        };
    }

    private static string? Field(IReaderRow csv, string column) =>
        csv.TryGetField<string>(
            csv.HeaderRecord?.FirstOrDefault(header => string.Equals(header, column, StringComparison.OrdinalIgnoreCase)) ?? column,
            out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static bool TryDate(string? value, out DateTimeOffset result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = default;
            return false;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out result);
    }
}

/// <summary>One raw row, before it is turned into an issue update.</summary>
public sealed record SpolRow
{
    public required long RowNumber { get; init; }

    public required string GroupId { get; init; }

    public string? Cph { get; init; }

    public string? RuleNo { get; init; }

    public string? Status { get; init; }

    public string? Notes { get; init; }

    public bool HasAssignee { get; init; }

    public string? AssignedTo { get; init; }

    public string? Priority { get; init; }

    public DateTimeOffset? DateReceived { get; init; }

    /// <summary>Used only to break ties between rows sharing a key.</summary>
    public DateTimeOffset? DateComplete { get; init; }

    public IReadOnlyList<ExternalContactEvent> Contacts { get; init; } = [];

    public bool IsKeyed => !string.IsNullOrWhiteSpace(Cph) && !string.IsNullOrWhiteSpace(RuleNo);

    /// <summary>
    /// Must agree with the rules engine's scheme, or every imported row would reconcile against
    /// nothing. The group is a column on the issue, not part of its identity.
    /// </summary>
    public IssueId ToIssueId() => IssueId.Derive(RuleNo!.Trim(), Cph!.Trim());
}
