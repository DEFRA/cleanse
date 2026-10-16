using System.Text;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Storage.Sqlite.Schema;
using Microsoft.Data.Sqlite;

namespace Cleanse.Storage.Sqlite.Stores;

/// <summary>
/// The SQLite rendering of <see cref="IssueFilter"/>. Context lookups go through the JSON1
/// functions rather than a jsonb operator, and the key itself is parameterised so a context key
/// can never reach the JSON path parser.
/// </summary>
internal static class SqliteIssueFilter
{
    internal sealed record Clause(string Sql, IReadOnlyList<(string Name, object Value)> Parameters)
    {
        public void Bind(SqliteCommand command)
        {
            foreach (var (name, value) in Parameters)
            {
                command.Bind(name, value);
            }
        }
    }

    public static Clause Build(IssueFilter filter)
    {
        var predicates = new List<string>();
        var parameters = new List<(string, object)>();

        void Add(string sql, string name, object value)
        {
            predicates.Add(sql);
            parameters.Add((name, value));
        }

        if (filter.GroupId is { } group)
        {
            Add("group_id = $f_group", "$f_group", group);
        }

        if (filter.RuleId is { } rule)
        {
            Add("rule_id = $f_rule", "$f_rule", rule);
        }

        if (filter.RunId is { } run)
        {
            Add("run_id = $f_run", "$f_run", run);
        }

        if (filter.Status is { } status)
        {
            Add("status = $f_status", "$f_status", status.ToWire());
        }

        if (filter.WorkflowStatuses is { Count: > 0 } workflow)
        {
            // SQLite has no array parameter, so the list is expanded into placeholders.
            var names = new List<string>(workflow.Count);
            var index = 0;
            foreach (var value in workflow)
            {
                var name = $"$f_workflow{index++}";
                names.Add(name);
                parameters.Add((name, value.ToWire()));
            }

            predicates.Add($"workflow_status IN ({string.Join(", ", names)})");
        }

        if (filter.AssignedTo is { } assignee)
        {
            Add("LOWER(assigned_to) = LOWER($f_assignee)", "$f_assignee", assignee);
        }

        if (filter.Unassigned is { } unassigned)
        {
            predicates.Add(unassigned
                ? "(assigned_to IS NULL OR assigned_to = '')"
                : "(assigned_to IS NOT NULL AND assigned_to <> '')");
        }

        if (filter.CreatedAfterUtc is { } createdAfter)
        {
            Add("created_at_utc >= $f_created_after", "$f_created_after", createdAfter.ToText());
        }

        if (filter.CreatedBeforeUtc is { } createdBefore)
        {
            Add("created_at_utc <= $f_created_before", "$f_created_before", createdBefore.ToText());
        }

        if (filter.UpdatedAfterUtc is { } updatedAfter)
        {
            Add("updated_at_utc >= $f_updated_after", "$f_updated_after", updatedAfter.ToText());
        }

        if (filter.UpdatedBeforeUtc is { } updatedBefore)
        {
            Add("updated_at_utc <= $f_updated_before", "$f_updated_before", updatedBefore.ToText());
        }

        if (filter.ContextMatches is { Count: > 0 } context)
        {
            var index = 0;
            foreach (var (key, value) in context)
            {
                var keyName = $"$f_ctx_k{index}";
                var valueName = $"$f_ctx_v{index}";
                index++;

                predicates.Add($"""
                    EXISTS (
                        SELECT 1 FROM json_each(issues.context) AS ctx
                        WHERE ctx.key = {keyName} AND ctx.value = {valueName}
                    )
                    """);
                parameters.Add((keyName, key));
                parameters.Add((valueName, value));
            }
        }

        if (filter.SearchTerm is { } term && !string.IsNullOrWhiteSpace(term))
        {
            // SQLite LIKE is already case-insensitive for ASCII, so no ILIKE equivalent is needed.
            predicates.Add("""
                (
                    id LIKE $f_term ESCAPE '\'
                    OR notes LIKE $f_term ESCAPE '\'
                    OR assigned_to LIKE $f_term ESCAPE '\'
                    OR EXISTS (
                        SELECT 1 FROM json_each(issues.context) AS ctx
                        WHERE ctx.value LIKE $f_term ESCAPE '\'
                    )
                )
                """);
            parameters.Add(("$f_term", $"%{EscapeLike(term.Trim())}%"));
        }

        var sql = predicates.Count == 0 ? string.Empty : "WHERE " + string.Join("\n  AND ", predicates);
        return new Clause(sql, parameters);
    }

    public static string OrderBy(IssueFilter filter)
    {
        var direction = filter.SortDirection == SortDirection.Descending ? "DESC" : "ASC";
        var column = filter.SortBy switch
        {
            IssueSortField.CreatedAtUtc => "created_at_utc",
            IssueSortField.Id => "id",
            IssueSortField.RuleId => "rule_id",
            IssueSortField.WorkflowStatus => "workflow_status",
            _ => "updated_at_utc"
        };

        return column == "id" ? $"id {direction}" : $"{column} {direction}, id ASC";
    }

    private static string EscapeLike(string term)
    {
        var builder = new StringBuilder(term.Length);
        foreach (var c in term)
        {
            if (c is '%' or '_' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
