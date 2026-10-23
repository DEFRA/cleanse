using Cleanse.Api.Diagnostics;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Cleanse.Api.V1;

public static class DiagnosticsEndpoints
{
    public static RouteGroupBuilder MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/diagnostics").WithTags("Diagnostics");

        group.MapGet(string.Empty, Get)
            .WithName("GetDiagnostics")
            .WithSummary("Database, import storage and KRDS health, issue counts and the last reset.")
            .WithDescription(
                "Never fails because a dependency has. Each probe reports 'ok', 'failed' or " +
                "'unknown' with a reason, so a status page can render a complete picture of a " +
                "partly broken service. 'database' queries the issue store, 'storage' checks the " +
                "import bucket can be reached, and 'krds' calls the KRDS Data Bridge's own /health " +
                "endpoint and names only its host.")
            .Produces<DiagnosticsDto>();

        return group;
    }

    private static async Task<IResult> Get(
        [FromQuery] string? groupId,
        ICleanseDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.QueryAsync(new GetDiagnosticsQuery(groupId), cancellationToken);
        return result.ToHttpResult();
    }
}
