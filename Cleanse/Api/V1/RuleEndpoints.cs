using Cleanse.Rules.Engine;

namespace Cleanse.Api.V1;

/// <summary>What a rule is, in the terms the business uses to talk about it.</summary>
public sealed record RuleDto(
    string RuleId,
    string RuleNumber,
    string Name,
    string ErrorCode,
    string ErrorDescription,
    string Summary,
    int Priority,
    string Status);

public static class RuleEndpoints
{
    public static RouteGroupBuilder MapRuleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/rules").WithTags("Rules");

        group.MapGet(string.Empty, GetRuleBook)
            .WithName("GetRuleBook")
            .WithSummary("The rule book: every rule, in execution order, including those on hold.")
            .Produces<IReadOnlyList<RuleDto>>();

        return group;
    }

    private static IResult GetRuleBook(CleanseRuleRegistry registry) =>
        TypedResults.Ok(registry.All.Select(rule => new RuleDto(
            rule.Descriptor.RuleId,
            rule.Descriptor.RuleNumber,
            rule.Descriptor.Name,
            rule.Descriptor.ErrorCode,
            rule.Descriptor.ErrorDescription,
            rule.Descriptor.Summary,
            rule.Priority,
            rule.Status.ToString().ToLowerInvariant())).ToArray());
}
