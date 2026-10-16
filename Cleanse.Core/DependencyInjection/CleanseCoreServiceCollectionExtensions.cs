using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands;
using Cleanse.Core.Commands.History;
using Cleanse.Core.Commands.Imports;
using Cleanse.Core.Commands.Issues;
using Cleanse.Core.Commands.Maintenance;
using Cleanse.Core.Commands.Runs;
using Cleanse.Core.Contracts;
using Cleanse.Core.Dispatching;
using Cleanse.Core.Queries.Audit;
using Cleanse.Core.Queries.History;
using Cleanse.Core.Queries.Imports;
using Cleanse.Core.Queries.Issues;
using Cleanse.Core.Queries.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cleanse.Core.DependencyInjection;

public static class CleanseCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the domain use cases. A storage provider must be registered separately: Core
    /// has no opinion about which one. Handlers are listed explicitly rather than discovered by
    /// scanning, so a missing registration is a compile error rather than a runtime surprise.
    /// </summary>
    public static IServiceCollection AddCleanseCore(
        this IServiceCollection services,
        Action<CleanseCoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new CleanseCoreOptions();
        configure?.Invoke(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddTransient<ICleanseDispatcher, CleanseDispatcher>();
        services.TryAddTransient<IssueMutator>();

        AddCommand<StartRunCommand, RuleRunDto, StartRunCommandHandler>(services);
        AddCommand<CompleteRunCommand, RuleRunDto, CompleteRunCommandHandler>(services);
        AddCommand<AbandonRunCommand, RuleRunDto, AbandonRunCommandHandler>(services);
        AddCommand<ReportRunProgressCommand, RuleRunDto, ReportRunProgressCommandHandler>(services);
        AddCommand<RequestRunCancellationCommand, RuleRunDto, RequestRunCancellationCommandHandler>(services);
        AddCommand<CancelRunCommand, RuleRunDto, CancelRunCommandHandler>(services);
        AddCommand<FailRunCommand, RuleRunDto, FailRunCommandHandler>(services);
        AddCommand<ReapStalledRunsCommand, int, ReapStalledRunsCommandHandler>(services);

        AddCommand<UpsertDetectionsCommand, DetectionUpsertResult, UpsertDetectionsCommandHandler>(services);
        AddCommand<AssignIssueCommand, IssueDto, AssignIssueCommandHandler>(services);
        AddCommand<SetIssueNotesCommand, IssueDto, SetIssueNotesCommandHandler>(services);
        AddCommand<SetIssuePriorityCommand, IssueDto, SetIssuePriorityCommandHandler>(services);
        AddCommand<SetIssueWorkflowStatusCommand, IssueDto, SetIssueWorkflowStatusCommandHandler>(services);
        AddCommand<SuppressIssueCommand, IssueDto, SuppressIssueCommandHandler>(services);
        AddCommand<UnsuppressIssueCommand, IssueDto, UnsuppressIssueCommandHandler>(services);
        AddCommand<ExpireSuppressionsCommand, SuppressionExpiryResult, ExpireSuppressionsCommandHandler>(services);

        AddCommand<AddIssueHistoryCommand, IssueHistoryEntryDto, AddIssueHistoryCommandHandler>(services);

        AddCommand<CreateImportJobCommand, ImportJobDto, CreateImportJobCommandHandler>(services);
        AddCommand<ApplyImportBatchCommand, ImportBatchResult, ApplyImportBatchCommandHandler>(services);
        AddCommand<CompleteImportJobCommand, ImportJobDto, CompleteImportJobCommandHandler>(services);
        AddCommand<FailImportJobCommand, ImportJobDto, FailImportJobCommandHandler>(services);
        AddCommand<ReapStalledImportsCommand, int, ReapStalledImportsCommandHandler>(services);

        AddCommand<ResetDataCommand, ResetResultDto, ResetDataCommandHandler>(services);

        AddQuery<GetIssueQuery, IssueDto, GetIssueQueryHandler>(services);
        AddQuery<SearchIssuesQuery, PagedResult<IssueDto>, SearchIssuesQueryHandler>(services);
        AddQuery<GetRunQuery, RuleRunDto, GetRunQueryHandler>(services);
        AddQuery<SearchRunsQuery, PagedResult<RuleRunSummaryDto>, SearchRunsQueryHandler>(services);
        AddQuery<GetLatestRunForGroupQuery, RuleRunDto, GetLatestRunForGroupQueryHandler>(services);
        AddQuery<GetGroupSummaryQuery, GroupSummaryDto, GetGroupSummaryQueryHandler>(services);
        AddQuery<GetIssueAuditQuery, PagedResult<AuditEntryDto>, GetIssueAuditQueryHandler>(services);
        AddQuery<GetRunAuditQuery, PagedResult<AuditEntryDto>, GetRunAuditQueryHandler>(services);
        AddQuery<GetIssueHistoryQuery, PagedResult<IssueHistoryEntryDto>, GetIssueHistoryQueryHandler>(services);

        AddQuery<GetImportJobQuery, ImportJobDto, GetImportJobQueryHandler>(services);
        AddQuery<SearchImportJobsQuery, PagedResult<ImportJobDto>, SearchImportJobsQueryHandler>(services);
        AddQuery<GetImportRejectionsQuery, PagedResult<ImportRejectionDto>, GetImportRejectionsQueryHandler>(services);

        return services;
    }

    private static void AddCommand<TCommand, TResult, THandler>(IServiceCollection services)
        where TCommand : ICommand<TResult>
        where THandler : class, ICommandHandler<TCommand, TResult> =>
        services.TryAddTransient<ICommandHandler<TCommand, TResult>, THandler>();

    private static void AddQuery<TQuery, TResult, THandler>(IServiceCollection services)
        where TQuery : IQuery<TResult>
        where THandler : class, IQueryHandler<TQuery, TResult> =>
        services.TryAddTransient<IQueryHandler<TQuery, TResult>, THandler>();
}
