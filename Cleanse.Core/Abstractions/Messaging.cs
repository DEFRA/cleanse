namespace Cleanse.Core.Abstractions;

/// <summary>A state-changing use case. <typeparamref name="TResult"/> fixes the handler's return.</summary>
public interface ICommand<out TResult>;

/// <summary>A read-only use case.</summary>
public interface IQuery<out TResult>;

public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken = default);
}

public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<Result<TResult>> HandleAsync(TQuery query, CancellationToken cancellationToken = default);
}

/// <summary>The single entry point consumers use. Rules engines and the eventual API both sit on this.</summary>
public interface ICleanseDispatcher
{
    Task<Result<TResult>> SendAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);

    Task<Result<TResult>> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default);
}
