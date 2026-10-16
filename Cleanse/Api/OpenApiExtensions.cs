using System.Text.Json.Nodes;
using Cleanse.Auth;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Imports;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Cleanse.Api;

public static class OpenApiExtensions
{
    /// <summary>
    /// The wire vocabularies, projected from the enums themselves rather than written out, so a
    /// new workflow status cannot be added without the document learning about it.
    /// </summary>
    private static class Vocabularies
    {
        public static readonly string[] IssueStatus =
            [.. Enum.GetValues<IssueStatus>().Select(StatusNames.ToWire)];

        public static readonly string[] WorkflowStatus =
            [.. Enum.GetValues<WorkflowStatus>().Select(StatusNames.ToWire)];

        public static readonly string[] Priority =
            [.. Enum.GetValues<IssuePriority>().Select(StatusNames.ToWire)];

        public static readonly string[] RunState =
            [.. Enum.GetNames<RunState>().Select(name => name.ToLowerInvariant())];

        public static readonly string[] ImportStatus =
            [.. Enum.GetNames<ImportJobStatus>().Select(name => name.ToLowerInvariant())];

        public static readonly string[] ActorKind =
            [.. Enum.GetNames<ActorKind>().Select(name => name.ToLowerInvariant())];

        public static readonly string[] AuditKind = Enum.GetNames<AuditEventKind>();

        public static readonly string[] HistoryActionType = Enum.GetNames<HistoryActionType>();

        public static readonly string[] IssueSortField = Enum.GetNames<IssueSortField>();

        public static readonly string[] SortDirection = Enum.GetNames<SortDirection>();
    }

    /// <summary>
    /// Which DTO properties carry a closed vocabulary. The DTOs expose these as strings on
    /// purpose, so that reordering an enum member cannot silently change the wire contract; the
    /// cost is that the permitted values have to be reattached here.
    /// </summary>
    private static readonly Dictionary<Type, Dictionary<string, string[]>> SchemaVocabularies = new()
    {
        [typeof(IssueDto)] = new()
        {
            ["status"] = Vocabularies.IssueStatus,
            ["workflowStatus"] = Vocabularies.WorkflowStatus,
            ["priority"] = Vocabularies.Priority
        },
        [typeof(IssueStatusCountDto)] = new()
        {
            ["status"] = Vocabularies.IssueStatus,
            ["workflowStatus"] = Vocabularies.WorkflowStatus
        },
        [typeof(RuleRunDto)] = new() { ["state"] = Vocabularies.RunState },
        [typeof(RuleRunSummaryDto)] = new() { ["state"] = Vocabularies.RunState },
        [typeof(ImportJobDto)] = new() { ["status"] = Vocabularies.ImportStatus },
        [typeof(AuditEntryDto)] = new()
        {
            ["kind"] = Vocabularies.AuditKind,
            ["actorKind"] = Vocabularies.ActorKind
        },
        [typeof(IssueHistoryEntryDto)] = new()
        {
            ["actionType"] = Vocabularies.HistoryActionType
        },
        [typeof(Contracts.SetWorkflowStatusRequest)] = new()
        {
            ["workflowStatus"] = Vocabularies.WorkflowStatus
        },
        [typeof(Contracts.AddHistoryRequest)] = new()
        {
            ["actionType"] = Vocabularies.HistoryActionType
        }
    };

    /// <summary>The same vocabularies, for the query string.</summary>
    private static readonly Dictionary<string, string[]> ParameterVocabularies = new(StringComparer.Ordinal)
    {
        ["status"] = Vocabularies.IssueStatus,
        ["workflowStatus"] = Vocabularies.WorkflowStatus,
        ["state"] = Vocabularies.RunState,
        ["sortBy"] = Vocabularies.IssueSortField,
        ["sortDirection"] = Vocabularies.SortDirection
    };

    /// <summary>
    /// Describes both authentication schemes, the response bodies and the closed vocabularies.
    /// Without this the generated spec lists the routes but tells a consumer nothing about what
    /// comes back or how to authenticate.
    /// </summary>
    public static IServiceCollection AddCleanseOpenApi(this IServiceCollection services) =>
        services.AddOpenApi("v1", options =>
        {
            options.AddDocumentTransformer(DescribeDocument);
            options.AddOperationTransformer(RequireAuthentication);
            options.AddOperationTransformer(AnnotateParameters);
            options.AddSchemaTransformer(AnnotateSchema);
        });

    private static Task DescribeDocument(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Cleanse API",
            Version = "v1",
            Description =
                "Detects and tracks data anomalies between systems. Issues are upserted by " +
                "overnight rule runs and triaged by people.\n\n" +
                "An issue carries two orthogonal states: `status`, owned by the rules engine, " +
                "and `workflowStatus`, owned by a caseworker. A re-detection never changes the " +
                "latter.\n\n" +
                "Mutations accept an `expectedVersion` and answer `409` when it does not match " +
                "the stored version. Errors are RFC 7807 problem documents carrying a stable " +
                "`code` extension."
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
        {
            [ApiKeyAuthenticationHandler.SchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = ApiKeyOptions.HeaderName,
                Description = "Static shared key issued to trusted callers."
            },
            [JwtBearerDefaults.AuthenticationScheme] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description =
                    "An AWS STS web identity token obtained with GetWebIdentityToken. " +
                    "Only allow-listed CDP services are accepted."
            }
        };

        return Task.CompletedTask;
    }

    /// <remarks>
    /// The scheme references are bound to the host document explicitly. An unbound reference
    /// cannot resolve its target and serialises as an empty object, which is how a document ends
    /// up appearing to place no security requirement on anything.
    /// </remarks>
    private static Task RequireAuthentication(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
        {
            return Task.CompletedTask;
        }

        var document = context.Document;

        operation.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(ApiKeyAuthenticationHandler.SchemeName, document)] = []
            },
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(JwtBearerDefaults.AuthenticationScheme, document)] = []
            }
        ];

        return Task.CompletedTask;
    }

    private static Task AnnotateParameters(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        foreach (var parameter in operation.Parameters ?? [])
        {
            if (parameter.Name is null ||
                !ParameterVocabularies.TryGetValue(parameter.Name, out var values))
            {
                continue;
            }

            // Repeatable filters arrive as an array, so the vocabulary belongs on the item.
            var target = parameter.Schema is { Type: JsonSchemaType.Array, Items: OpenApiSchema items }
                ? items
                : parameter.Schema as OpenApiSchema;

            if (target is not null)
            {
                target.Enum = ToJson(values);
            }
        }

        return Task.CompletedTask;
    }

    private static Task AnnotateSchema(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (!SchemaVocabularies.TryGetValue(context.JsonTypeInfo.Type, out var properties))
        {
            return Task.CompletedTask;
        }

        foreach (var (name, values) in properties)
        {
            if (schema.Properties?.TryGetValue(name, out var property) is true &&
                property is OpenApiSchema concrete)
            {
                concrete.Enum = ToJson(values);
            }
        }

        return Task.CompletedTask;
    }

    private static List<JsonNode> ToJson(string[] values) =>
        [.. values.Select(value => (JsonNode)JsonValue.Create(value))];
}
