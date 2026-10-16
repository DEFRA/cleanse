using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Imports;

namespace Cleanse.Core.Commands.Imports;

/// <summary>
/// Registers an upload that has already been written to the file store. Fails when the group
/// has a job in flight, so a double-click cannot start two workers over the same issues.
/// </summary>
public sealed record CreateImportJobCommand(
    Guid ImportJobId,
    string GroupId,
    string FileName,
    string BlobKey,
    int TotalRows,
    Actor Actor) : ICommand<ImportJobDto>;

internal sealed class CreateImportJobCommandHandler(IImportJobStore imports, TimeProvider clock)
    : ICommandHandler<CreateImportJobCommand, ImportJobDto>
{
    public async Task<Result<ImportJobDto>> HandleAsync(
        CreateImportJobCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Actor);

        if (string.IsNullOrWhiteSpace(command.GroupId))
        {
            return DomainErrors.Validation("An import must be scoped to a group.");
        }

        var inFlight = await imports.GetActiveForGroupAsync(command.GroupId.Trim(), cancellationToken);
        if (inFlight is not null)
        {
            return DomainErrors.ImportAlreadyInProgress(command.GroupId.Trim(), inFlight.Id);
        }

        var job = ImportJob.Create(
            command.ImportJobId,
            command.GroupId,
            command.FileName,
            command.BlobKey,
            command.Actor.Id,
            command.TotalRows,
            clock.GetUtcNow());

        // The unique index is the real guard; the check above only gives a better error message.
        if (!await imports.TryCreateAsync(job, cancellationToken))
        {
            return DomainErrors.ImportAlreadyInProgress(job.GroupId, job.Id);
        }

        return job.ToDto();
    }
}

public sealed record CompleteImportJobCommand(Guid ImportJobId) : ICommand<ImportJobDto>;

internal sealed class CompleteImportJobCommandHandler(IImportJobStore imports, TimeProvider clock)
    : ICommandHandler<CompleteImportJobCommand, ImportJobDto>
{
    public async Task<Result<ImportJobDto>> HandleAsync(
        CompleteImportJobCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var job = await imports.GetAsync(command.ImportJobId, cancellationToken);
        if (job is null)
        {
            return DomainErrors.ImportNotFound(command.ImportJobId);
        }

        var completed = job.Complete(clock.GetUtcNow());
        if (completed.IsFailure)
        {
            return completed.ToFailure<ImportJobDto>();
        }

        await imports.SaveAsync(job, cancellationToken);
        return job.ToDto();
    }
}

public sealed record FailImportJobCommand(Guid ImportJobId, string Reason) : ICommand<ImportJobDto>;

internal sealed class FailImportJobCommandHandler(IImportJobStore imports, TimeProvider clock)
    : ICommandHandler<FailImportJobCommand, ImportJobDto>
{
    public async Task<Result<ImportJobDto>> HandleAsync(
        FailImportJobCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var job = await imports.GetAsync(command.ImportJobId, cancellationToken);
        if (job is null)
        {
            return DomainErrors.ImportNotFound(command.ImportJobId);
        }

        var failed = job.Fail(command.Reason, clock.GetUtcNow());
        if (failed.IsFailure)
        {
            return failed.ToFailure<ImportJobDto>();
        }

        await imports.SaveAsync(job, cancellationToken);
        return job.ToDto();
    }
}

/// <summary>
/// Reclaims jobs whose worker stopped reporting. Without this a container killed mid-import
/// leaves a job that looks alive forever and blocks the group's unique index.
/// </summary>
public sealed record ReapStalledImportsCommand(TimeSpan StaleAfter, int Limit = 50)
    : ICommand<int>;

internal sealed class ReapStalledImportsCommandHandler(IImportJobStore imports, TimeProvider clock)
    : ICommandHandler<ReapStalledImportsCommand, int>
{
    public async Task<Result<int>> HandleAsync(
        ReapStalledImportsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var now = clock.GetUtcNow();
        var stalled = await imports.FindStalledAsync(now - command.StaleAfter, command.Limit, cancellationToken);

        var reaped = 0;
        foreach (var job in stalled)
        {
            var abandoned = job.Abandon(
                $"No progress reported for {command.StaleAfter.TotalMinutes:0} minutes.", now);

            if (abandoned.IsFailure)
            {
                continue;
            }

            await imports.SaveAsync(job, cancellationToken);
            reaped++;
        }

        return reaped;
    }
}
