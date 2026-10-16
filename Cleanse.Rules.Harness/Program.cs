using System.Diagnostics;
using System.Globalization;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Runs;
using Cleanse.Core.DependencyInjection;
using Cleanse.Core.Domain;
using Cleanse.Core.Queries.Issues;
using Cleanse.Core.Queries.Runs;
using Cleanse.Rules;
using Cleanse.Rules.DependencyInjection;
using Cleanse.Rules.Engine;
using Cleanse.Rules.Krds;
using Cleanse.Storage.Sqlite;
using Cleanse.Storage.Sqlite.Schema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cleanse.Rules.Harness;

/// <summary>
/// Runs the cts-sam rules engine against a real KRDS snapshot, persisting to a throwaway SQLite
/// database. Exists so the engine can be exercised end to end on a developer machine, where the
/// KRDS Data Bridge is not reachable.
/// </summary>
internal static class Program
{
    private const string DefaultSnapshot =
        @"C:\livestock\data\sqlite_databases\krds-db_20260929070002.sqlite";

    private static async Task<int> Main(string[] args)
    {
        var snapshot = args.ElementAtOrDefault(0) ?? DefaultSnapshot;
        var database = args.ElementAtOrDefault(1) ?? Path.Combine(Path.GetTempPath(), "cleanse-harness.db");
        var fresh = !args.Contains("--keep", StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(snapshot))
        {
            Console.Error.WriteLine($"Snapshot not found: {snapshot}");
            Console.Error.WriteLine("Usage: Cleanse.Rules.Harness [snapshot.sqlite] [cleanse.db] [--keep]");
            return 1;
        }

        if (fresh && File.Exists(database))
        {
            File.Delete(database);
        }

        Console.WriteLine($"Snapshot : {snapshot}");
        Console.WriteLine($"Cleanse  : {database}{(fresh ? " (recreated)" : " (reused)")}");
        Console.WriteLine();

        await using var provider = BuildProvider(snapshot, database);
        await provider.GetRequiredService<CleanseSqliteDatabase>().InitialiseAsync();

        var dispatcher = provider.GetRequiredService<ICleanseDispatcher>();
        var engine = provider.GetRequiredService<CtsSamRuleEngine>();

        var started = await dispatcher.SendAsync(
            new StartRunCommand(CleanseRuleRegistry.CtsSamGroupId, Actor.System));

        if (started.IsFailure)
        {
            Console.Error.WriteLine($"Could not start a run: {started.Error}");
            return 1;
        }

        var runId = started.Value.RunId;
        Console.WriteLine($"Run {runId} started.");

        var stopwatch = Stopwatch.StartNew();
        using var progress = ReportProgress(dispatcher, runId);
        var result = await engine.ExecuteAsync(runId, Actor.System);
        stopwatch.Stop();

        Console.WriteLine();
        Console.WriteLine($"Outcome          : {result.Outcome}");
        Console.WriteLine($"Elapsed          : {stopwatch.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"Records scanned  : {result.RecordsScanned:N0}");
        Console.WriteLine($"Findings raised  : {result.FindingsRaised:N0}");
        Console.WriteLine($"Issues created   : {result.IssuesCreated:N0}");
        Console.WriteLine($"Reactivated      : {result.IssuesReactivated:N0}");
        Console.WriteLine($"Deactivated      : {result.IssuesDeactivated:N0}");

        if (result.FailureMessage is not null)
        {
            Console.WriteLine($"Failure          : {result.FailureMessage}");
        }

        await PrintByRuleAsync(dispatcher);
        await PrintRunAsync(dispatcher, runId);

        return result.Outcome == RuleEngineOutcome.Completed ? 0 : 1;
    }

    private static ServiceProvider BuildProvider(string snapshot, string database)
    {
        var services = new ServiceCollection();

        services.AddLogging(logging => logging
            .AddSimpleConsole(console => console.SingleLine = true)
            .SetMinimumLevel(LogLevel.Information));

        services.AddCleanseCore();
        services.AddCleanseSqliteStorage(sqlite => sqlite.DataSource = database);

        services.AddCleanseRules(new CleanseRulesOptions
        {
            // The harness drives the run itself; the scheduler would only get in the way.
            Enabled = false,
            GroupId = CleanseRuleRegistry.CtsSamGroupId
        });

        services.AddKrdsDataSource(new KrdsOptions
        {
            Provider = nameof(KrdsSnapshotProvider.LocalFile),
            LocalFilePath = snapshot
        });

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = false
        });
    }

    private static Timer ReportProgress(ICleanseDispatcher dispatcher, string runId) =>
        new(async _ =>
        {
            var run = await dispatcher.QueryAsync(new GetRunQuery(runId));
            if (run.IsFailure)
            {
                return;
            }

            var eta = run.Value.EstimatedCompletionAtUtc?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "-";
            Console.WriteLine(
                $"  {run.Value.ProgressPercent,6:F2}%  {run.Value.RecordsProcessed,7:N0}/{run.Value.RecordsTotal:N0}  " +
                $"eta {eta}  {run.Value.CurrentStage}");
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    private static async Task PrintByRuleAsync(ICleanseDispatcher dispatcher)
    {
        Console.WriteLine();
        Console.WriteLine("Active issues by rule:");

        foreach (var rule in CleanseRuleRegistry.CreateDefaultRules().OrderBy(r => r.Descriptor.RuleNumber))
        {
            var filter = new IssueFilter
            {
                GroupId = CleanseRuleRegistry.CtsSamGroupId,
                RuleId = rule.Descriptor.RuleId,
                Status = IssueStatus.Active,
                Page = new PageRequest(1, 1)
            };

            var page = await dispatcher.QueryAsync(new SearchIssuesQuery(filter));
            var count = page.IsSuccess ? page.Value.TotalCount : -1;

            Console.WriteLine(
                $"  rule {rule.Descriptor.RuleNumber} ({rule.Descriptor.ErrorCode}) " +
                $"{rule.Descriptor.ErrorDescription,-26} {count,6:N0}");
        }
    }

    private static async Task PrintRunAsync(ICleanseDispatcher dispatcher, string runId)
    {
        var run = await dispatcher.QueryAsync(new GetRunQuery(runId));
        if (run.IsFailure)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Run state {run.Value.State}, {run.Value.ProgressPercent:F2}%, " +
            $"{run.Value.ElapsedSeconds:F1}s, stage '{run.Value.CurrentStage}'.");
    }
}
