using Cleanse.Auth;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;

namespace Cleanse.Api.V1;

public static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/users").WithTags("Users")
            .RequireAuthorization(CleansePolicies.UserDirectory);

        group.MapPost("/logins", RecordLogin)
            .WithName("RecordUserLogin")
            .WithSummary("Record a validated Entra login attested by cleanse-app.")
            .WithDescription("Atomic upsert by issuer, clientId and subject. Requires cleanse-app service identity; API keys require Development or explicit local Docker trust configuration.")
            .Produces<UserDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet(string.Empty, Search)
            .WithName("SearchUsers")
            .WithSummary("Page known users within an exact tenant and client scope.")
            .Produces<PagedResult<UserDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        return group;
    }

    private static async Task<IResult> RecordLogin(
        RecordUserLoginRequest request, IUserDirectory directory, CancellationToken cancellationToken)
    {
        var result = await directory.RecordLoginAsync(request, cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<IResult> Search(
        IUserDirectory directory, CancellationToken cancellationToken,
        string? tenantId = null, string? clientId = null,
        int? pageNumber = null, int? page = null, int pageSize = 50)
    {
        var number = pageNumber ?? page ?? 1;
        if (number < 1 || pageSize < 1 || pageSize > PageRequest.MaxPageSize ||
            (long)(number - 1) * pageSize > int.MaxValue ||
            (pageNumber.HasValue && page.HasValue && pageNumber != page))
        {
            return TypedResults.Problem(title: "Invalid request", statusCode: 400,
                detail: "Use a positive pageNumber and pageSize from 1 to 500; page is an alias and must agree.");
        }

        var result = await directory.SearchAsync(
            tenantId ?? string.Empty, clientId ?? string.Empty, new PageRequest(number, pageSize), cancellationToken);
        return result.ToHttpResult();
    }
}
