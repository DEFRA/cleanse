using System.Security.Claims;
using Cleanse.Api.Imports;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Imports;
using Cleanse.Core.Contracts;
using Cleanse.Core.Queries.Imports;
using Microsoft.AspNetCore.Mvc;

namespace Cleanse.Api.V1;

public static class ImportEndpoints
{
    public static RouteGroupBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/imports").WithTags("Imports");

        group.MapPost(string.Empty, Upload)
            .WithName("UploadImport")
            .WithSummary("Upload an external extract for a group.")
            .Produces<ImportJobDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .DisableAntiforgery();

        group.MapGet(string.Empty, Search)
            .WithName("SearchImports")
            .WithSummary("List imports, newest first.")
            .Produces<PagedResult<ImportJobDto>>();
        group.MapGet("/{id:guid}", Get)
            .WithName("GetImport")
            .WithSummary("Import status and progress.")
            .Produces<ImportJobDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/{id:guid}/rejections", GetRejections)
            .WithName("GetImportRejections")
            .WithSummary("Rows the import could not apply.")
            .Produces<PagedResult<ImportRejectionDto>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> Upload(
        IFormFile file,
        [FromForm] string groupId,
        ClaimsPrincipal user,
        ICleanseDispatcher dispatcher,
        IImportFileStore files,
        ImportOptions options,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return Problem("No file was supplied.", StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(groupId))
        {
            return Problem("A groupId is required.", StatusCodes.Status400BadRequest);
        }

        if (file.Length > options.MaxUploadBytes)
        {
            return Problem(
                $"The file is {file.Length} bytes; the limit is {options.MaxUploadBytes}.",
                StatusCodes.Status413PayloadTooLarge);
        }

        // Validated before anything is persisted, so a renamed binary never reaches the store.
        await using (var probe = file.OpenReadStream())
        {
            if (!SpolCsvReader.TryValidateHeader(probe, out var headerError))
            {
                return Problem(headerError!, StatusCodes.Status400BadRequest);
            }
        }

        int totalRows;
        await using (var counting = file.OpenReadStream())
        {
            totalRows = SpolCsvReader.Read(counting, groupId).Count();
        }

        var jobId = Guid.CreateVersion7();
        var blobKey = $"imports/{groupId.Trim()}/{jobId}.csv";

        await using (var content = file.OpenReadStream())
        {
            await files.WriteAsync(blobKey, content, cancellationToken);
        }

        var result = await dispatcher.SendAsync(
            new CreateImportJobCommand(
                jobId, groupId, file.FileName, blobKey, totalRows, user.ToActor()),
            cancellationToken);

        return result.ToHttpResult(job => TypedResults.Accepted($"/v1/imports/{job.Id}", job));
    }

    private static async Task<IResult> Get(
        Guid id,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(new GetImportJobQuery(id), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> Search(
        [FromQuery(Name = "groupId")] string? groupId,
        [AsParameters] PageParameters page,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new SearchImportJobsQuery(groupId, page.ToPageRequest()), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> GetRejections(
        Guid id,
        [AsParameters] PageParameters page,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(
            new GetImportRejectionsQuery(id, page.ToPageRequest()), cancellationToken);
        return result.ToHttpResult();
    }

    private static IResult Problem(string detail, int status) =>
        TypedResults.Problem(title: "Invalid request", detail: detail, statusCode: status);
}
