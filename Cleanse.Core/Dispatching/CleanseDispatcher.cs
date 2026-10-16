using System.Collections.Concurrent;
using Cleanse.Core.Abstractions;

namespace Cleanse.Core.Dispatching;

/// <summary>
/// Resolves the handler for a command or query from the container. The closed generic invoker
/// for each message type is built once and cached, so dispatch costs a dictionary lookup and a
/// virtual call rather than reflection per message.
/// </summary>
internal sealed class CleanseDispatcher(IServiceProvider serviceProvider) : ICleanseDispatcher
{
    private static readonly ConcurrentDictionary<Type, object> CommandInvokers = new();
    private static readonly ConcurrentDictionary<Type, object> QueryInvokers = new();

    public Task<Result<TResult>> SendAsync<TResult>(
        ICommand<TResult> command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var invoker = (CommandInvoker<TResult>)CommandInvokers.GetOrAdd(
            command.GetType(),
            static type => Activator.CreateInstance(
                typeof(CommandInvoker<,>).MakeGenericType(type, typeof(TResult)), nonPublic: true)!);

        return invoker.InvokeAsync(serviceProvider, command, cancellationToken);
    }

    public Task<Result<TResult>> QueryAsync<TResult>(
        IQuery<TResult> query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var invoker = (QueryInvoker<TResult>)QueryInvokers.GetOrAdd(
            query.GetType(),
            static type => Activator.CreateInstance(
                typeof(QueryInvoker<,>).MakeGenericType(type, typeof(TResult)), nonPublic: true)!);

        return invoker.InvokeAsync(serviceProvider, query, cancellationToken);
    }

    private static TService Resolve<TService>(IServiceProvider provider, Type messageType) where TService : class =>
        provider.GetService(typeof(TService)) as TService
        ?? throw new InvalidOperationException(
            $"No handler of type {typeof(TService)} is registered for {messageType}. " +
            "Did you forget to call AddCleanseCore()?");

    private abstract class CommandInvoker<TResult>
    {
        public abstract Task<Result<TResult>> InvokeAsync(
            IServiceProvider provider,
            ICommand<TResult> command,
            CancellationToken cancellationToken);
    }

    private sealed class CommandInvoker<TCommand, TResult> : CommandInvoker<TResult>
        where TCommand : ICommand<TResult>
    {
        public override Task<Result<TResult>> InvokeAsync(
            IServiceProvider provider,
            ICommand<TResult> command,
            CancellationToken cancellationToken) =>
            Resolve<ICommandHandler<TCommand, TResult>>(provider, typeof(TCommand))
                .HandleAsync((TCommand)command, cancellationToken);
    }

    private abstract class QueryInvoker<TResult>
    {
        public abstract Task<Result<TResult>> InvokeAsync(
            IServiceProvider provider,
            IQuery<TResult> query,
            CancellationToken cancellationToken);
    }

    private sealed class QueryInvoker<TQuery, TResult> : QueryInvoker<TResult>
        where TQuery : IQuery<TResult>
    {
        public override Task<Result<TResult>> InvokeAsync(
            IServiceProvider provider,
            IQuery<TResult> query,
            CancellationToken cancellationToken) =>
            Resolve<IQueryHandler<TQuery, TResult>>(provider, typeof(TQuery))
                .HandleAsync((TQuery)query, cancellationToken);
    }
}
