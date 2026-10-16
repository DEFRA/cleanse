using System.Text.RegularExpressions;
using Cleanse.Storage.Postgres.Schema;
using Cleanse.Storage.Postgres.Stores;
using Npgsql;

namespace Cleanse.Storage.Postgres.Test;

/// <summary>
/// Guards the agreement between the issue column list, the VALUES placeholders and the parameters
/// actually bound. Runs without Docker on purpose: this mismatch only surfaces against a real
/// server, as <c>42601: INSERT has more target columns than expressions</c>, so a developer with
/// no Docker would otherwise see the suite skip and push it green.
/// </summary>
public class IssueInsertShapeTests
{
    [Fact]
    public void One_placeholder_is_emitted_for_every_issue_column()
    {
        Assert.Equal(Db.Issue.All.Length, Placeholders(PostgresIssueStore.IssueValuesClause()).Length);
    }

    [Fact]
    public void Every_placeholder_is_backed_by_a_bound_parameter()
    {
        using var command = new NpgsqlCommand();
        PostgresIssueStore.AddIssueParameters(command, SampleRow());

        var bound = command.Parameters.Select(parameter => parameter.ParameterName).ToArray();

        Assert.Equal(Placeholders(PostgresIssueStore.IssueValuesClause()), bound);
    }

    [Fact]
    public void The_batched_form_binds_the_same_shape_without_colliding_between_rows()
    {
        using var command = new NpgsqlCommand();
        PostgresIssueStore.AddIssueParameters(command, SampleRow(), "0");
        PostgresIssueStore.AddIssueParameters(command, SampleRow(), "1");

        var first = Placeholders(PostgresIssueStore.IssueValuesClause("0"));
        var second = Placeholders(PostgresIssueStore.IssueValuesClause("1"));

        Assert.Equal(Db.Issue.All.Length, first.Length);
        Assert.Empty(first.Intersect(second, StringComparer.Ordinal));
        Assert.Equal(
            first.Concat(second),
            command.Parameters.Select(parameter => parameter.ParameterName));
    }

    [Fact]
    public void The_column_list_is_rendered_from_the_order_it_declares()
    {
        Assert.Equal(string.Join(", ", Db.Issue.All), Db.Issue.AllColumns);
        Assert.Contains(Db.Issue.ActivatedAtUtc, Db.Issue.All);
        Assert.Contains(Db.Issue.DeactivatedAtUtc, Db.Issue.All);
    }

    private static string[] Placeholders(string valuesClause) =>
        Regex.Matches(valuesClause, "@([a-z_0-9]+)", RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value)
            .ToArray();

    private static IssueRow SampleRow() => new()
    {
        Id = "1-ABC",
        GroupId = "g",
        RuleId = "1",
        RunId = "run",
        Status = "open",
        WorkflowStatus = "todo",
        Context = "{}",
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
        UpdatedAtUtc = DateTimeOffset.UnixEpoch,
        ActivatedAtUtc = DateTimeOffset.UnixEpoch,
        Version = 1
    };
}
