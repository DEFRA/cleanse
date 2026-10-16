using Cleanse.Rules.Krds;
using Cleanse.Rules.Krds.Snapshots;
using Microsoft.Extensions.DependencyInjection;

namespace Cleanse.Rules.Test;

/// <summary>
/// Reads a real KRDS snapshot. Skipped where one is not present, so the suite still runs on a
/// machine without the data, but fails loudly on one that has it if the adapter regresses.
/// </summary>
public class KrdsSqliteDataSourceTests
{
    private const string SnapshotPath =
        @"C:\livestock\data\sqlite_databases\krds-db_20260929070002.sqlite";

    /// <summary>Counts verified directly against this snapshot with the SQLite CLI.</summary>
    private const int ExpectedRecords = 41_280;

    private const int ExpectedRule02 = 67;
    private const int ExpectedRule01 = 310;

    private static bool SnapshotAvailable => File.Exists(SnapshotPath);

    [Fact]
    public async Task Counts_only_records_with_a_real_cph()
    {
        Assert.SkipUnless(SnapshotAvailable, $"No KRDS snapshot at {SnapshotPath}.");

        await using var source = await Factory().OpenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ExpectedRecords, await source.CountRecordsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Streams_exactly_the_records_it_counted()
    {
        Assert.SkipUnless(SnapshotAvailable, $"No KRDS snapshot at {SnapshotPath}.");

        await using var source = await Factory().OpenAsync(TestContext.Current.CancellationToken);

        var streamed = 0;
        await foreach (var record in source.StreamRecordsAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(record.Cph.HasValue);
            Assert.False(string.IsNullOrWhiteSpace(record.CtsLocationNumber));
            streamed++;
        }

        Assert.Equal(ExpectedRecords, streamed);
    }

    [Fact]
    public async Task Produces_the_expected_findings_for_the_whole_snapshot()
    {
        Assert.SkipUnless(SnapshotAvailable, $"No KRDS snapshot at {SnapshotPath}.");

        var registry = new Engine.CleanseRuleRegistry(Engine.CleanseRuleRegistry.CreateDefaultRules());
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        await using var source = await Factory().OpenAsync(TestContext.Current.CancellationToken);

        await foreach (var record in source.StreamRecordsAsync(TestContext.Current.CancellationToken))
        {
            foreach (var finding in registry.Evaluate(new Groups.CtsSam.CtsSamRuleContext(record)))
            {
                counts[finding.Descriptor.RuleNumber] = counts.GetValueOrDefault(finding.Descriptor.RuleNumber) + 1;
            }
        }

        Assert.Equal(ExpectedRule02, counts.GetValueOrDefault("2"));
        Assert.Equal(ExpectedRule01, counts.GetValueOrDefault("1"));
    }

    [Fact]
    public async Task Excludes_slaughterhouses_by_rejecting_their_bare_numeric_cph()
    {
        Assert.SkipUnless(SnapshotAvailable, $"No KRDS snapshot at {SnapshotPath}.");

        await using var source = await Factory().OpenAsync(TestContext.Current.CancellationToken);

        await foreach (var record in source.StreamRecordsAsync(TestContext.Current.CancellationToken))
        {
            Assert.DoesNotContain("SH-", record.CtsLocationNumber, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Does_not_delete_a_snapshot_it_did_not_download()
    {
        Assert.SkipUnless(SnapshotAvailable, $"No KRDS snapshot at {SnapshotPath}.");

        var source = await Factory().OpenAsync(TestContext.Current.CancellationToken);
        await source.DisposeAsync();

        Assert.True(File.Exists(SnapshotPath));
    }

    private static KrdsCtsSamDataSourceFactory Factory()
    {
        var services = new ServiceCollection();
        var problem = services.AddKrdsDataSource(new KrdsOptions
        {
            Provider = nameof(KrdsSnapshotProvider.LocalFile),
            LocalFilePath = SnapshotPath
        });

        Assert.Null(problem);

        var provider = services.BuildServiceProvider();
        return new KrdsCtsSamDataSourceFactory(provider.GetRequiredService<IKrdsSnapshotSource>());
    }
}
