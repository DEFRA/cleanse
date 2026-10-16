using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Api.Imports;

/// <summary>
/// Turns raw rows into issue updates. The collapse rule is deterministic so that importing the
/// same extract twice produces identical state, which is the whole point of the exercise.
/// </summary>
public static class SpolRowMapper
{
    /// <summary>
    /// Most advanced human outcome wins. 18 keys in the source extract appear twice, and without
    /// a fixed precedence two runs of the same file could disagree.
    /// </summary>
    private static int Rank(WorkflowStatus? status) => status switch
    {
        WorkflowStatus.Resolved => 4,
        WorkflowStatus.Nonactionable => 3,
        WorkflowStatus.InProgress => 2,
        WorkflowStatus.Todo => 1,
        _ => 0
    };

    public static WorkflowStatus? MapStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "new" => WorkflowStatus.Todo,
        "in progress" => WorkflowStatus.InProgress,
        "completed" => WorkflowStatus.Resolved,
        "unable to action" => WorkflowStatus.Nonactionable,
        _ => null
    };

    /// <summary>Source values carry a rank prefix, such as "4. Low"; only the word matters.</summary>
    public static IssuePriority? MapPriority(string? priority)
    {
        if (string.IsNullOrWhiteSpace(priority))
        {
            return null;
        }

        var word = priority.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[^1];
        return StatusNames.TryParsePriority(word, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Collapses rows sharing a key, then projects to the external state Core applies. Rows
    /// without a usable key are returned separately so they can be reported rather than dropped.
    /// </summary>
    public static SpolBatch Project(IEnumerable<SpolRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var byKey = new Dictionary<string, SpolRow>(StringComparer.Ordinal);
        var invalid = new List<SpolRow>();
        var collapsed = 0;

        foreach (var row in rows)
        {
            if (!row.IsKeyed)
            {
                invalid.Add(row);
                continue;
            }

            var key = row.ToIssueId().Value;
            if (!byKey.TryGetValue(key, out var existing))
            {
                byKey[key] = row;
                continue;
            }

            collapsed++;
            if (Wins(row, existing))
            {
                byKey[key] = row;
            }
        }

        var projected = byKey.Values
            .Select(row => new ExternalIssueRow
            {
                RowNumber = row.RowNumber,
                IssueId = row.ToIssueId(),
                WorkflowStatus = MapStatus(row.Status),
                ExternalNotes = row.Notes,
                Priority = MapPriority(row.Priority),
                ReceivedAtUtc = row.DateReceived,
                Contacts = row.Contacts
            })
            .ToList();

        return new SpolBatch(projected, invalid, collapsed);
    }

    private static bool Wins(SpolRow candidate, SpolRow incumbent)
    {
        var byStatus = Rank(MapStatus(candidate.Status)).CompareTo(Rank(MapStatus(incumbent.Status)));
        if (byStatus != 0)
        {
            return byStatus > 0;
        }

        var byCompletion = Nullable.Compare(candidate.DateComplete, incumbent.DateComplete);
        if (byCompletion != 0)
        {
            return byCompletion > 0;
        }

        // Last tie-break is file order, so the outcome never depends on dictionary ordering.
        return candidate.RowNumber > incumbent.RowNumber;
    }
}

public sealed record SpolBatch(
    IReadOnlyList<ExternalIssueRow> Rows,
    IReadOnlyList<SpolRow> Invalid,
    int DuplicatesCollapsed);
