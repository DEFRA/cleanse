using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Cleanse.Api.V1;

public sealed record PageParameters(
    [FromQuery(Name = "pageNumber")] int? PageNumber,
    [FromQuery(Name = "pageSize")] int? PageSize)
{
    public PageRequest ToPageRequest() => new(
        Math.Max(PageNumber ?? 1, 1),
        Math.Clamp(PageSize ?? PageRequest.Default.PageSize, 1, PageRequest.MaxPageSize));
}

/// <summary>
/// The query-string surface of the run history. States are parsed explicitly so an unknown one
/// produces a 400 naming it rather than silently matching nothing.
/// </summary>
public sealed record RunSearchParameters(
    [FromQuery(Name = "groupId")] string? GroupId,
    [FromQuery(Name = "state")] string[]? State,
    [FromQuery(Name = "pageNumber")] int? PageNumber,
    [FromQuery(Name = "pageSize")] int? PageSize)
{
    public bool TryParseStates(out RunState[] states, out string? invalid)
    {
        invalid = null;
        states = [];

        if (State is not { Length: > 0 })
        {
            return true;
        }

        var parsed = new List<RunState>(State.Length);
        foreach (var value in State)
        {
            if (!Enum.TryParse<RunState>(value, ignoreCase: true, out var state))
            {
                invalid = value;
                return false;
            }

            parsed.Add(state);
        }

        states = [.. parsed];
        return true;
    }

    public PageRequest ToPageRequest() => new PageParameters(PageNumber, PageSize).ToPageRequest();
}

/// <summary>
/// The query-string surface of <see cref="IssueFilter"/>. Parsing is explicit so an unknown
/// status produces a 400 naming the offending value rather than silently matching nothing.
/// </summary>
public sealed record IssueSearchParameters(
    [FromQuery(Name = "groupId")] string? GroupId,
    [FromQuery(Name = "ruleId")] string? RuleId,
    [FromQuery(Name = "runId")] string? RunId,
    [FromQuery(Name = "status")] string? Status,
    [FromQuery(Name = "workflowStatus")] string[]? WorkflowStatus,
    [FromQuery(Name = "assignedTo")] string? AssignedTo,
    [FromQuery(Name = "unassigned")] bool? Unassigned,
    [FromQuery(Name = "searchTerm")] string? SearchTerm,
    [FromQuery(Name = "createdAfterUtc")] DateTimeOffset? CreatedAfterUtc,
    [FromQuery(Name = "createdBeforeUtc")] DateTimeOffset? CreatedBeforeUtc,
    [FromQuery(Name = "updatedAfterUtc")] DateTimeOffset? UpdatedAfterUtc,
    [FromQuery(Name = "updatedBeforeUtc")] DateTimeOffset? UpdatedBeforeUtc,
    [FromQuery(Name = "sortBy")] string? SortBy,
    [FromQuery(Name = "sortDirection")] string? SortDirection,
    [FromQuery(Name = "pageNumber")] int? PageNumber,
    [FromQuery(Name = "pageSize")] int? PageSize)
{
    public bool TryBuildFilter(out IssueFilter filter, out string? error)
    {
        filter = new IssueFilter();
        error = null;

        IssueStatus? status = null;
        if (!string.IsNullOrWhiteSpace(Status))
        {
            if (!StatusNames.TryParseIssueStatus(Status, out var parsed))
            {
                error = $"'{Status}' is not a known status.";
                return false;
            }

            status = parsed;
        }

        List<WorkflowStatus>? workflowStatuses = null;
        if (WorkflowStatus is { Length: > 0 })
        {
            workflowStatuses = [];
            foreach (var value in WorkflowStatus)
            {
                if (!StatusNames.TryParseWorkflowStatus(value, out var parsed))
                {
                    error = $"'{value}' is not a known workflow status.";
                    return false;
                }

                workflowStatuses.Add(parsed);
            }
        }

        var sortBy = IssueSortField.UpdatedAtUtc;
        if (!string.IsNullOrWhiteSpace(SortBy) &&
            !Enum.TryParse(SortBy, ignoreCase: true, out sortBy))
        {
            error = $"'{SortBy}' is not a sortable field.";
            return false;
        }

        var sortDirection = Core.Abstractions.SortDirection.Descending;
        if (!string.IsNullOrWhiteSpace(SortDirection) &&
            !Enum.TryParse(SortDirection, ignoreCase: true, out sortDirection))
        {
            error = $"'{SortDirection}' is not a sort direction.";
            return false;
        }

        filter = new IssueFilter
        {
            GroupId = Blank(GroupId),
            RuleId = Blank(RuleId),
            RunId = Blank(RunId),
            Status = status,
            WorkflowStatuses = workflowStatuses,
            AssignedTo = Blank(AssignedTo),
            Unassigned = Unassigned,
            SearchTerm = Blank(SearchTerm),
            CreatedAfterUtc = CreatedAfterUtc,
            CreatedBeforeUtc = CreatedBeforeUtc,
            UpdatedAfterUtc = UpdatedAfterUtc,
            UpdatedBeforeUtc = UpdatedBeforeUtc,
            SortBy = sortBy,
            SortDirection = sortDirection,
            Page = new PageParameters(PageNumber, PageSize).ToPageRequest()
        };

        return true;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
