using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Commands.Issues;

/// <summary>
/// The scheduled sweep that returns issues whose temporary suppression has lapsed to the triage
/// queue. Safe to run as often as you like; it only touches issues whose expiry has passed.
/// </summary>
public sealed record ExpireSuppressionsCommand(Actor Actor) : ICommand<SuppressionExpiryResult>;

internal sealed class ExpireSuppressionsCommandHandler(
    IIssueStore issues,
    IAuditStore audit,
    TimeProvider clock,
    CleanseCoreOptions options)
    : ICommandHandler<ExpireSuppressionsCommand, SuppressionExpiryResult>
{
    public async Task<Result<SuppressionExpiryResult>> HandleAsync(
        ExpireSuppressionsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Actor);

        var now = clock.GetUtcNow();
        var expired = 0;

        for (var batch = 0; batch < options.MaxSuppressionExpiryBatches; batch++)
        {
            var candidates = await issues.FindExpiredSuppressionsAsync(
                now, options.SuppressionExpiryBatchSize, cancellationToken);

            if (candidates.Count == 0)
            {
                break;
            }

            var toSave = new List<Issue>(candidates.Count);
            var entries = new List<AuditEntry>(candidates.Count);

            foreach (var issue in candidates)
            {
                var change = issue.ExpireSuppression(now);
                if (!change.IsMaterial)
                {
                    continue;
                }

                toSave.Add(issue);
                entries.Add(AuditEntry.ForIssue(issue.Id, change, command.Actor, now));
            }

            // Nothing moved despite a non-empty candidate set: stop rather than spin on it.
            if (toSave.Count == 0)
            {
                break;
            }

            await issues.SaveBatchAsync(toSave, cancellationToken);
            await audit.AppendBatchAsync(entries, cancellationToken);
            expired += toSave.Count;

            if (candidates.Count < options.SuppressionExpiryBatchSize)
            {
                break;
            }
        }

        return new SuppressionExpiryResult(expired);
    }
}
