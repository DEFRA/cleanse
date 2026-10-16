using System.Diagnostics.CodeAnalysis;

namespace Cleanse.Core;

/// <summary>
/// Expected domain failures travel as values rather than exceptions, so the eventual API tier
/// can map an error code straight onto a ProblemDetails response without catch blocks on the
/// hot path.
/// </summary>
public sealed class Result<T>
{
    private readonly T? _value;

    private Result(bool isSuccess, T? value, DomainError? error)
    {
        IsSuccess = isSuccess;
        _value = value;
        Error = error;
    }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess { get; }

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => !IsSuccess;

    public DomainError? Error { get; }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Result is a failure ({Error}); there is no value.");

    public static Result<T> Success(T value) => new(true, value, null);

    public static Result<T> Failure(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(false, default, error);
    }

    public Result<TOther> ToFailure<TOther>() => Result<TOther>.Failure(
        Error ?? throw new InvalidOperationException("Result is a success; there is no error."));

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(DomainError error) => Failure(error);
}
