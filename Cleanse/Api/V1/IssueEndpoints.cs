using System.Security.Claims;
using Cleanse.Api.Contracts;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.History;
using Cleanse.Core.Commands.Issues;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Queries.Audit;
using Cleanse.Core.Queries.History;
using Cleanse.Core.Queries.Issues;
using Microsoft.AspNetCore.Mvc;

namespace Cleanse.Api.V1;

public static class IssueEndpoints
{
    public static RouteGroupBuilder MapIssueEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/issues").WithTags("Issues");

        group.MapGet(string.Empty, Search)
            .WithName("SearchIssues")
            .WithSummary("Search issues.")
            .Produces<PagedResult<IssueDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
        group.MapGet("/{id}", GetById)
            .WithName("GetIssue")
            .WithSummary("Fetch a single issue.")
            .Produces<IssueDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPut("/{id}/assignee", Assign)
            .WithName("AssignIssue")
            .WithSummary("Assign or unassign an issue.")
            .Produces<IssueDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPut("/{id}/notes", SetNotes)
            .WithName("SetIssueNotes")
            .WithSummary("Replace an issue's notes.")
            .Produces<IssueDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPut("/{id}/workflow-status", SetWorkflowStatus)
            .WithName("SetIssueWorkflowStatus")
            .WithSummary("Move an issue along the human triage axis.")
            .Produces<IssueDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPut("/{id}/suppression", Suppress)
            .WithName("SuppressIssue")
            .WithSummary("Suppress an issue until an expiry.")
            .Produces<IssueDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapDelete("/{id}/suppression", Unsuppress)
            .WithName("UnsuppressIssue")
            .WithSummary("Lift a suppression early.")
            .Produces<IssueDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapGet("/{id}/audit", GetAudit)
            .WithName("GetIssueAudit")
            .WithSummary("System change log for an issue.")
            .Produces<PagedResult<AuditEntryDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/{id}/history", GetHistory)
            .WithName("GetIssueHistory")
            .WithSummary("Human case actions for an issue.")
            .Produces<PagedResult<IssueHistoryEntryDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/{id}/history", AddHistory)
            .WithName("AddIssueHistory")
            .WithSummary("Record a human case action.")
            .Produces<IssueHistoryEntryDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    /// <summary>
    /// Issue ids are URL-safe by construction, so the route value needs no post-processing. The
    /// method exists to keep that assumption in one place rather than spread across ten handlers.
    /// </summary>
    private static IssueId ParseIssueId(string id) => IssueId.From(id);

    private static async Task<IResult> Search(
        [AsParameters] IssueSearchParameters parameters,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        if (!parameters.TryBuildFilter(out var filter, out var error))
        {
            return TypedResults.Problem(title: "Invalid request", detail: error, statusCode: 400);
        }

        var result = await dispatcher.QueryAsync(new SearchIssuesQuery(filter), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> GetById(
        string id,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(new GetIssueQuery(ParseIssueId(id)), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> Assign(
        string id,
        AssignIssueRequest request,
        ClaimsPrincipal user,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new AssignIssueCommand(ParseIssueId(id), request.AssignTo, user.ToActor(), request.ExpectedVersion),
            cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> SetNotes(
        string id,
        SetNotesRequest request,
        ClaimsPrincipal user,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new SetIssueNotesCommand(ParseIssueId(id), request.Notes, user.ToActor(), request.ExpectedVersion),
            cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> SetWorkflowStatus(
        string id,
        SetWorkflowStatusRequest request,
        ClaimsPrincipal user,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        if (!StatusNames.TryParseWorkflowStatus(request.WorkflowStatus, out var status))
        {
            return TypedResults.Problem(
                title: "Invalid request",
                detail: $"'{request.WorkflowStatus}' is not a known workflow status.",
                statusCode: 400);
        }

        var result = await dispatcher.SendAsync(
            new SetIssueWorkflowStatusCommand(ParseIssueId(id), status, user.ToActor(), request.ExpectedVersion),
            cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> Suppress(
        string id,
        SuppressIssueRequest request,
        ClaimsPrincipal user,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new SuppressIssueCommand(
                ParseIssueId(id), request.ExpiresAtUtc, user.ToActor(), request.ExpectedVersion),
            cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> Unsuppress(
        string id,
        [FromQuery] int? expectedVersion,
        ClaimsPrincipal user,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new UnsuppressIssueCommand(ParseIssueId(id), user.ToActor(), expectedVersion),
            cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> GetAudit(
        string id,
        [AsParameters] PageParameters page,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new GetIssueAuditQuery(ParseIssueId(id), page.ToPageRequest()), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> GetHistory(
        string id,
        [AsParameters] PageParameters page,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new GetIssueHistoryQuery(ParseIssueId(id), page.ToPageRequest()), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> AddHistory(
        string id,
        AddHistoryRequest request,
        ClaimsPrincipal user,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<HistoryActionType>(request.ActionType, ignoreCase: true, out var actionType))
        {
            return TypedResults.Problem(
                title: "Invalid request",
                detail: $"'{request.ActionType}' is not a known action type.",
                statusCode: 400);
        }

        var result = await dispatcher.SendAsync(
            new AddIssueHistoryCommand(ParseIssueId(id), actionType, request.Comments, user.ToActor()),
            cancellationToken);

        return result.ToHttpResult(entry => TypedResults.Created($"/v1/issues/{id}/history", entry));
    }
}
