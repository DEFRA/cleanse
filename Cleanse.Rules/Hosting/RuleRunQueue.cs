using System.Threading.Channels;
using Cleanse.Core.Domain;

namespace Cleanse.Rules.Hosting;

/// <summary>A queued run, and who asked for it.</summary>
public sealed record RuleRunRequest(string RunId, Actor Actor);

/// <summary>
/// Hands a started run to the background executor.
/// </summary>
/// <remarks>
/// In-process and unbounded, which is safe only because Core already guarantees one run per
/// group at a time: the queue can never hold more than a handful of items. It exists so that an
/// adhoc trigger returns a run id immediately instead of holding the HTTP request open for the
/// length of a run.
/// </remarks>
public sealed class RuleRunQueue
{
    private readonly Channel<RuleRunRequest> _channel =
        Channel.CreateUnbounded<RuleRunRequest>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(RuleRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _channel.Writer.TryWrite(request);
    }

    public IAsyncEnumerable<RuleRunRequest> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
