using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Imports;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Imports;

namespace Cleanse.Api.Imports;

/// <summary>
/// Leases pending imports and applies them. Runs in the background so an upload returns straight
/// away: 30,000 rows cannot be processed inside API Gateway's 29 second ceiling.
/// </summary>
public sealed class ImportProcessorService(
    IServiceScopeFactory scopeFactory,
    ImportOptions options,
    TimeProvider clock,
    ILogger<ImportProcessorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReapStalledAsync(stoppingToken);

                if (!await TryProcessNextAsync(stoppingToken))
                {
                    await Task.Delay(options.PollInterval, clock, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let one bad job kill the loop; the next pass picks up where this left off.
                logger.LogError(ex, "The import processor loop failed and will retry.");
                await Task.Delay(options.PollInterval, clock, stoppingToken);
            }
        }
    }

    private async Task ReapStalledAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICleanseDispatcher>();

        var result = await dispatcher.SendAsync(
            new ReapStalledImportsCommand(options.StaleAfter), cancellationToken);

        if (result.IsSuccess && result.Value > 0)
        {
            logger.LogWarning(
                "Reclaimed {Count} import(s) whose worker stopped reporting.", result.Value);
        }
    }

    private async Task<bool> TryProcessNextAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var provider = scope.ServiceProvider;
        var imports = provider.GetRequiredService<IImportJobStore>();

        var job = await imports.LeaseNextPendingAsync(clock.GetUtcNow(), cancellationToken);
        if (job is null)
        {
            return false;
        }

        var dispatcher = provider.GetRequiredService<ICleanseDispatcher>();
        logger.LogInformation(
            "Processing import {ImportJobId} for group {GroupId}.", job.Id, job.GroupId);

        try
        {
            await ProcessAsync(provider, dispatcher, job, cancellationToken);
            await dispatcher.SendAsync(new CompleteImportJobCommand(job.Id), cancellationToken);
            logger.LogInformation("Import {ImportJobId} completed.", job.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import {ImportJobId} failed.", job.Id);
            await dispatcher.SendAsync(
                new FailImportJobCommand(job.Id, ex.Message), CancellationToken.None);
        }

        return true;
    }

    private async Task ProcessAsync(
        IServiceProvider provider,
        ICleanseDispatcher dispatcher,
        ImportJob job,
        CancellationToken cancellationToken)
    {
        var files = provider.GetRequiredService<IImportFileStore>();
        var imports = provider.GetRequiredService<IImportJobStore>();

        SpolBatch batch;
        await using (var content = await files.OpenReadAsync(job.BlobKey, cancellationToken))
        {
            // Collapsing duplicates needs the whole file in view, so rows are materialised before
            // chunking. 30,000 rows is a few megabytes; streaming would make the rule unstable.
            batch = SpolRowMapper.Project(SpolCsvReader.Read(content, job.GroupId));
        }

        await RecordUnkeyedRowsAsync(imports, job, batch, cancellationToken);

        foreach (var chunk in batch.Rows.Chunk(options.BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await dispatcher.SendAsync(
                new ApplyImportBatchCommand(job.Id, chunk, Actor.Machine(SpolCsvReader.Source)),
                cancellationToken);

            if (result.IsFailure)
            {
                throw new InvalidOperationException(
                    $"Applying a batch of {chunk.Length} rows failed: {result.Error}");
            }
        }
    }

    /// <summary>
    /// Rows with no CPH or rule number, plus the duplicates that lost the collapse. Counted up
    /// front so the progress percentage can still reach 100.
    /// </summary>
    private async Task RecordUnkeyedRowsAsync(
        IImportJobStore imports,
        ImportJob job,
        SpolBatch batch,
        CancellationToken cancellationToken)
    {
        if (batch.Invalid.Count > 0)
        {
            var rejections = batch.Invalid
                .Take(options.MaxRejectionsRecorded)
                .Select(row => new ImportJobRejection(
                    job.Id,
                    row.RowNumber,
                    null,
                    ImportRejectionReasons.InvalidRow,
                    $"Row has no usable key (CPH '{row.Cph ?? "<blank>"}', rule '{row.RuleNo ?? "<blank>"}')."))
                .ToList();

            await imports.AddRejectionsAsync(rejections, cancellationToken);
        }

        if (batch.Invalid.Count == 0 && batch.DuplicatesCollapsed == 0)
        {
            return;
        }

        var progress = job.RecordProgress(
            ImportJobCounters.Empty with
            {
                ProcessedRows = batch.Invalid.Count + batch.DuplicatesCollapsed,
                Invalid = batch.Invalid.Count,
                DuplicatesCollapsed = batch.DuplicatesCollapsed
            },
            clock.GetUtcNow());

        if (progress.IsSuccess)
        {
            await imports.SaveAsync(job, cancellationToken);
        }
    }
}
