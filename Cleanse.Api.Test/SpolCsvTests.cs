using System.Text;
using Cleanse.Api.Imports;
using Cleanse.Core.Domain;

namespace Cleanse.Api.Test;

/// <summary>
/// Parsing and collapse, driven by the shapes the real 30,000 row extract actually contains:
/// duplicate keys, numbered priorities, embedded newlines in notes, and malformed CPHs.
/// </summary>
public class SpolCsvTests
{
    private const string Header =
        "Date Received,Priority,CPH,Rule No,Rule Name,Error Code,Error Code Description," +
        "Status,Assigned to,1st Contact Date,2nd Contact Date,3rd Contact Date," +
        "Letter/email sent,Notes,Date Complete";

    private static Stream Csv(params string[] rows) =>
        new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", [Header, .. rows])));

    private static string Row(
        string cph,
        string ruleNo,
        string status,
        string notes = "",
        string priority = "4. Low",
        string received = "2025-11-27T00:00:00Z",
        string first = "",
        string second = "",
        string third = "",
        string letter = "",
        string complete = "") =>
        $"{received},{priority},{cph},{ruleNo},Rule Name,1,Desc,{status},a@b.gov.uk," +
        $"{first},{second},{third},{letter},\"{notes}\",{complete}";

    [Fact]
    public void Duplicate_winner_carries_its_assignee_and_headers_are_case_insensitive()
    {
        var text = string.Join("\n", Header.Replace("Assigned to", "ASSIGNED TO", StringComparison.Ordinal),
            Row("assignment", "1", "New").Replace("a@b.gov.uk", "First Person", StringComparison.Ordinal),
            Row("assignment", "1", "Completed").Replace("a@b.gov.uk", "  Winner Person  ", StringComparison.Ordinal));
        using var csv = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var batch = SpolRowMapper.Project(SpolCsvReader.Read(csv, "group"));
        var winner = Assert.Single(batch.Rows);
        Assert.True(winner.HasAssignee);
        Assert.Equal("Winner Person", winner.AssignedTo);
        Assert.Equal(1, batch.DuplicatesCollapsed);
    }

    [Fact]
    public void The_header_is_validated_before_anything_is_stored()
    {
        var valid = Csv(Row("01/036/8000", "1", "Completed"));
        Assert.True(SpolCsvReader.TryValidateHeader(valid, out _));

        var notCsv = new MemoryStream(Encoding.UTF8.GetBytes("MZ\x90\x00binary rubbish"));
        Assert.False(SpolCsvReader.TryValidateHeader(notCsv, out var error));
        Assert.Contains("missing required columns", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_key_matches_the_scheme_the_rules_engine_uses()
    {
        var rows = SpolCsvReader.Read(Csv(Row("01/036/8000", "1", "Completed")), "cts-sam").ToList();

        var id = Assert.Single(rows).ToIssueId();

        // The group is not part of the key. If it were, an imported row could never reconcile
        // against the issue the engine raised for the same anomaly.
        Assert.Equal(IssueId.Derive("1", "01/036/8000"), id);
        Assert.True(IssueId.IsUrlSafe(id.Value), id.Value);
    }

    [Theory]
    [InlineData("New", WorkflowStatus.Todo)]
    [InlineData("In Progress", WorkflowStatus.InProgress)]
    [InlineData("Completed", WorkflowStatus.Resolved)]
    [InlineData("Unable to Action", WorkflowStatus.Nonactionable)]
    public void Source_statuses_map_onto_the_workflow_axis(string source, WorkflowStatus expected) =>
        Assert.Equal(expected, SpolRowMapper.MapStatus(source));

    [Fact]
    public void An_unrecognised_or_blank_status_maps_to_nothing_rather_than_guessing() =>
        Assert.Null(SpolRowMapper.MapStatus("   "));

    [Theory]
    [InlineData("4. Low", IssuePriority.Low)]
    [InlineData("3. Medium", IssuePriority.Medium)]
    [InlineData("1. High", IssuePriority.High)]
    [InlineData("Low", IssuePriority.Low)]
    public void Numbered_priorities_lose_their_rank_prefix(string source, IssuePriority expected) =>
        Assert.Equal(expected, SpolRowMapper.MapPriority(source));

    [Fact]
    public void A_blank_priority_stays_null() => Assert.Null(SpolRowMapper.MapPriority(""));

    [Fact]
    public void Contact_dates_become_history_events_without_inferring_a_channel()
    {
        var rows = SpolCsvReader.Read(
            Csv(Row("01/036/8000", "1", "Completed",
                first: "2026-01-19T00:00:00Z",
                second: "2026-03-31T23:00:00Z",
                letter: "2026-04-06T23:00:00Z")),
            "cts-sam").ToList();

        var contacts = Assert.Single(rows).Contacts;

        Assert.Equal(3, contacts.Count);
        Assert.Equal(HistoryActionType.PhoneCall, contacts[0].ActionType);
        Assert.Equal("1st Contact Date", contacts[0].Column);
        Assert.Equal(HistoryActionType.LetterSent, contacts[^1].ActionType);
        Assert.Equal("Letter/email sent", contacts[^1].Column);
    }

    [Fact]
    public void Notes_containing_newlines_survive_parsing()
    {
        // The real extract is 50,000 lines for 30,000 rows because of exactly this.
        var multiline = "1st attempt - no answer\n\n2nd attempt - spoke to keeper";
        var rows = SpolCsvReader.Read(Csv(Row("01/045/0197", "1", "Completed", multiline)), "cts-sam").ToList();

        var row = Assert.Single(rows);
        Assert.Contains("2nd attempt", row.Notes!, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_keys_collapse_to_the_most_advanced_outcome()
    {
        // Mirrors 06/206/0015 rule 9, the one duplicate pair that disagrees on status.
        var batch = SpolRowMapper.Project(SpolCsvReader.Read(
            Csv(
                Row("06/206/0015", "9", "Unable to Action", "Landline disconnected"),
                Row("06/206/0015", "9", "Completed", "Mobile already on SAM. NFA")),
            "cts-sam"));

        var row = Assert.Single(batch.Rows);
        Assert.Equal(WorkflowStatus.Resolved, row.WorkflowStatus);
        Assert.Equal("Mobile already on SAM. NFA", row.ExternalNotes);
        Assert.Equal(1, batch.DuplicatesCollapsed);
    }

    [Fact]
    public void Collapse_does_not_depend_on_file_order()
    {
        var forwards = SpolRowMapper.Project(SpolCsvReader.Read(
            Csv(Row("06/206/0015", "9", "New"), Row("06/206/0015", "9", "Completed")), "cts-sam"));

        var backwards = SpolRowMapper.Project(SpolCsvReader.Read(
            Csv(Row("06/206/0015", "9", "Completed"), Row("06/206/0015", "9", "New")), "cts-sam"));

        Assert.Equal(forwards.Rows[0].WorkflowStatus, backwards.Rows[0].WorkflowStatus);
        Assert.Equal(WorkflowStatus.Resolved, forwards.Rows[0].WorkflowStatus);
    }

    [Fact]
    public void Rows_without_a_usable_key_are_reported_rather_than_dropped()
    {
        var batch = SpolRowMapper.Project(SpolCsvReader.Read(
            Csv(
                Row("", "1", "Completed"),
                Row("01/036/8000", "", "Completed"),
                Row("01/036/8000", "1", "Completed")),
            "cts-sam"));

        Assert.Single(batch.Rows);
        Assert.Equal(2, batch.Invalid.Count);
    }

    [Fact]
    public void A_malformed_cph_still_produces_a_key_so_it_can_be_reported_as_unmatched()
    {
        // C3161 appears in the real extract; it will simply not match an issue.
        var batch = SpolRowMapper.Project(
            SpolCsvReader.Read(Csv(Row("C3161", "4", "Completed")), "cts-sam"));

        Assert.Single(batch.Rows);
        Assert.Empty(batch.Invalid);
    }

    [Fact]
    public void Projection_is_byte_identical_across_repeated_runs()
    {
        var rows = new[]
        {
            Row("01/036/8000", "1", "Completed", "note one", first: "2026-01-19T00:00:00Z"),
            Row("06/206/0015", "9", "Unable to Action", "note two"),
            Row("06/206/0015", "9", "Completed", "note three"),
            Row("", "2", "New")
        };

        var first = SpolRowMapper.Project(SpolCsvReader.Read(Csv(rows), "cts-sam"));
        var second = SpolRowMapper.Project(SpolCsvReader.Read(Csv(rows), "cts-sam"));

        // Compared field by field: the record holds a List, so its generated equality would
        // compare that member by reference and pass regardless of content.
        Assert.Equal(Fingerprint(first), Fingerprint(second));
        Assert.Equal(first.DuplicatesCollapsed, second.DuplicatesCollapsed);
        Assert.Equal(first.Invalid.Count, second.Invalid.Count);
    }

    private static string Fingerprint(SpolBatch batch) =>
        string.Join("\n", batch.Rows
            .OrderBy(r => r.IssueId.Value, StringComparer.Ordinal)
            .Select(r => string.Join('|',
                r.IssueId.Value,
                r.WorkflowStatus,
                r.ExternalNotes,
                r.Priority,
                r.ReceivedAtUtc?.ToString("O"),
                string.Join(',', r.Contacts.Select(c => $"{c.Column}@{c.AtUtc:O}:{c.ActionType}")))));
}
