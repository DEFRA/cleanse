using System.Security.Claims;
using Cleanse.Api.Contracts;
using Cleanse.Auth;
using Cleanse.Core;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Issues;
using Cleanse.Core.Commands.Maintenance;
using Cleanse.Core.Commands.Runs;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Queries.Audit;
using Cleanse.Core.Queries.Runs;
using Cleanse.Rules;
using Cleanse.Rules.Hosting;
using Microsoft.AspNetCore.Mvc;

namespace Cleanse.Api.V1;

public static class RunEndpoints
{
    public static RouteGroupBuilder MapRunEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/runs").WithTags("Runs");

        group.MapPost(string.Empty, Start)
            .WithName("StartRun")
            .WithSummary("Open a run for a group.")
            .Produces<RuleRunDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapGet(string.Empty, Search)
            .WithName("SearchRuns")
            .WithSummary("Run history, newest first, including anything running now.")
            .Produces<PagedResult<RuleRunSummaryDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
        group.MapGet("/{runId}", Get)
            .WithName("GetRun")
            .WithSummary("Fetch a run and its counters.")
            .Produces<RuleRunDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/{runId}/audit", GetAudit)
            .WithName("GetRunAudit")
            .WithSummary("Run lifecycle events.")
            .Produces<PagedResult<AuditEntryDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/trigger", Trigger)
            .WithName("TriggerRun")
            .WithSummary("Start the built-in rules engine for its group, in the background.")
            .Produces<RuleRunDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapPost("/{runId}/cancel", Cancel)
            .WithName("CancelRun")
            .WithSummary("Ask a running run to stop at its next batch boundary.")
            .Produces<RuleRunDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{runId}/detections", UpsertDetections)
            .WithName("UpsertDetections")
            .WithSummary("Upsert a batch of detections into an open run.")
            .Produces<DetectionUpsertResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        group.MapPost("/{runId}/complete", Complete)
            .WithName("CompleteRun")
            .WithSummary("Close a run and deactivate anything it did not re-detect.")
            .Produces<RuleRunDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPost("/{runId}/abandon", Abandon)
            .WithName("AbandonRun")
            .WithSummary("End a run without sweeping, after a partial or failed pass.")
            .Produces<RuleRunDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return group;
    }

    private static async Task<IResult> Search(
        [AsParameters] RunSearchParameters parameters,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        if (!parameters.TryParseStates(out var states, out var invalid))
        {
            return TypedResults.Problem(new ProblemDetails
            {
                Title = "Invalid request",
                Detail = $"'{invalid}' is not a run state.",
                Status = StatusCodes.Status400BadRequest,
                Extensions = { ["code"] = ErrorCodes.ValidationFailed }
            });
        }

        var result = await dispatcher.QueryAsync(
            new SearchRunsQuery(parameters.GroupId, states, parameters.ToPageRequest()), cancellationToken);

        return result.ToHttpResult();
    }

    /// <summary>
    /// Returns as soon as the run exists. The work happens on a background worker, so the caller
    /// gets an id to poll rather than an open connection for the length of a run.
    /// </summary>
    private static async Task<IResult> Trigger(
        ClaimsPrincipal user,
        IRuleEngineLauncher launcher,
        CancellationToken cancellationToken)
    {
        var result = await launcher.TryStartAsync(user.ToActor(), cancellationToken);
        return result.ToHttpResult(run => TypedResults.Accepted($"/v1/runs/{run.RunId}", run));
    }

    private static async Task<IResult> Cancel(
        string runId,
        ClaimsPrincipal user,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new RequestRunCancellationCommand(runId, user.ToActor()), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> Start(
        StartRunRequest request,
        ClaimsPrincipal user,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new StartRunCommand(request.GroupId, user.ToActor(), request.RunId), cancellationToken);

        return result.ToHttpResult(run => TypedResults.Created($"/v1/runs/{run.RunId}", run));
    }

    private static async Task<IResult> Get(
        string runId,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(new GetRunQuery(runId), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> GetAudit(
        string runId,
        [AsParameters] PageParameters page,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new GetRunAuditQuery(runId, page.ToPageRequest()), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> UpsertDetections(
        string runId,
        UpsertDetectionsRequest request,
        ClaimsPrincipal user,
        ICleanseDispatcher dispatcher,
        ApiOptions options,
        CancellationToken cancellationToken)
    {
        if (request.Detections.Count > options.MaxDetectionsPerRequest)
        {
            // 413 with the limit in the body, so a caller can adapt without reading the docs.
            return TypedResults.Problem(new ProblemDetails
            {
                Title = "Batch too large",
                Detail =
                    $"This request carries {request.Detections.Count} detections; the limit is " +
                    $"{options.MaxDetectionsPerRequest}. Split the batch across several requests.",
                Status = StatusCodes.Status413PayloadTooLarge,
                Extensions =
                {
                    ["code"] = "detections.batch_too_large",
                    ["limit"] = options.MaxDetectionsPerRequest,
                    ["supplied"] = request.Detections.Count
                }
            });
        }

        var unsafeId = request.Detections
            .FirstOrDefault(d => !IssueId.IsUrlSafe(d.Id?.Trim()))?.Id;

        if (unsafeId is not null)
        {
            // Rejected at the edge: an unaddressable id would only fail later, on a GET nobody
            // can form. IssueId.Derive produces a safe id for any input.
            return TypedResults.Problem(new ProblemDetails
            {
                Title = "Invalid request",
                Detail =
                    $"Detection id '{unsafeId}' is not URL-safe. Ids may contain only letters, " +
                    "digits and the characters - . _ ~",
                Status = StatusCodes.Status400BadRequest,
                Extensions = { ["code"] = ErrorCodes.ValidationFailed }
            });
        }

        var detections = request.Detections.Select(d => d.ToDetection()).ToArray();
        var result = await dispatcher.SendAsync(
            new UpsertDetectionsCommand(runId, detections, user.ToActor()), cancellationToken);

        return result.ToHttpResult();
    }

    private static async Task<IResult> Complete(
        string runId,
        ClaimsPrincipal user,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new CompleteRunCommand(runId, user.ToActor()), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> Abandon(
        string runId,
        AbandonRunRequest request,
        ClaimsPrincipal user,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new AbandonRunCommand(runId, request.Reason, user.ToActor()), cancellationToken);
        return result.ToHttpResult();
    }
}

/// <summary>A rule group, which is the unit of scheduling and of issue scoping.</summary>
public sealed record GroupDto(string GroupId, string Name, bool RulesEnabled);

public static class GroupEndpoints
{
    public static RouteGroupBuilder MapGroupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/groups").WithTags("Groups");

        group.MapGet(string.Empty, GetGroups)
            .WithName("ListGroups")
            .WithSummary("The rule groups this deployment knows about.")
            .Produces<IReadOnlyList<GroupDto>>();
        group.MapGet("/{groupId}/summary", GetSummary)
            .WithName("GetGroupSummary")
            .WithSummary("Issue counts by status and the latest run for a group.")
            .Produces<GroupSummaryDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/{groupId}/runs/latest", GetLatestRun)
            .WithName("GetLatestRunForGroup")
            .WithSummary("The most recently started run for a group.")
            .Produces<RuleRunDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    /// <summary>
    /// A list rather than a constant so a consumer can tell whether a group selector is worth
    /// showing. Only <c>cts-sam</c> exists today.
    /// </summary>
    private static IResult GetGroups(CleanseRulesOptions rules) =>
        TypedResults.Ok<IReadOnlyList<GroupDto>>(
            [new GroupDto(rules.GroupId, "CTS and SAM", rules.Enabled)]);

    private static async Task<IResult> GetSummary(
        string groupId,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(new GetGroupSummaryQuery(groupId), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> GetLatestRun(
        string groupId,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new GetLatestRunForGroupQuery(groupId), cancellationToken);
        return result.ToHttpResult();
    }
}

public static class MaintenanceEndpoints
{
    public static RouteGroupBuilder MapMaintenanceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/maintenance").WithTags("Maintenance");

        group.MapPost("/expire-suppressions", ExpireSuppressions)
            .WithName("ExpireSuppressions")
            .WithSummary("Return issues whose temporary suppression has lapsed to the triage queue.")
            .Produces<SuppressionExpiryResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/reset", Reset)
            .WithName("ResetData")
            .WithSummary("Permanently delete issues and everything recorded against them.")
            .WithDescription(
                "Irreversible. Deletes issues, case history, the audit trail, rule runs and " +
                "import jobs. Requires the x-reset-key header in addition to normal " +
                "authentication. Omit groupId to clear every group.")
            .Produces<ResetResultDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return group;
    }

    private static async Task<IResult> ExpireSuppressions(
        ClaimsPrincipal user,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.SendAsync(
            new ExpireSuppressionsCommand(user.ToActor()), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> Reset(
        ResetRequest? request,
        HttpContext http,
        ClaimsPrincipal user,
        ResetKeyOptions resetKey,
        ICleanseDispatcher dispatcher,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cleanse.Api.Reset");

        if (!resetKey.IsConfigured)
        {
            return Refused(
                "reset.not_configured",
                "Resetting the database is unavailable because no reset key has been set. An " +
                "administrator must configure the Auth:Reset:Key secret before this endpoint " +
                "will accept anything.");
        }

        var supplied = http.Request.Headers[ResetKeyOptions.HeaderName].ToString();

        if (string.IsNullOrWhiteSpace(supplied))
        {
            return Refused(
                "reset.key_required",
                $"The {ResetKeyOptions.HeaderName} header is required to reset the database.");
        }

        if (!resetKey.Matches(supplied))
        {
            // Never echo the supplied value; it may be a near miss of the real key.
            logger.LogWarning("Rejected a database reset carrying an invalid reset key.");
            return Refused("reset.key_invalid", "The supplied reset key is not valid.");
        }

        var actor = user.ToActor();
        var result = await dispatcher.SendAsync(
            new ResetDataCommand(request?.GroupId, actor), cancellationToken);

        if (result.IsSuccess)
        {
            // The audit trail is one of the things this destroys, so the shipped log is the only
            // durable record that it happened.
            logger.LogWarning(
                "Database reset by {ActorId} over scope {Scope}: deleted {Issues} issues, " +
                "{HistoryEntries} history entries, {AuditEntries} audit entries, {RuleRuns} runs " +
                "and {ImportJobs} import jobs.",
                actor.Id,
                result.Value.Scope,
                result.Value.Issues,
                result.Value.IssueHistoryEntries,
                result.Value.AuditEntries,
                result.Value.RuleRuns,
                result.Value.ImportJobs);
        }

        return result.ToHttpResult();
    }

    private static IResult Refused(string code, string detail) =>
        TypedResults.Problem(new ProblemDetails
        {
            Title = "Forbidden",
            Detail = detail,
            Status = StatusCodes.Status403Forbidden,
            Extensions = { ["code"] = code }
        });
}
