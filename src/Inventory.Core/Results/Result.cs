using System.Diagnostics.CodeAnalysis;

namespace Inventory.Core.Results;

/// <summary>
/// Either a value or an <see cref="InventoryFailure"/>. Expected failures (upstream down, bad data)
/// travel through the application as values, so no layer relies on exceptions for control flow.
/// </summary>
public sealed class Result<T>
{
    private Result(T? value, InventoryFailure? failure)
    {
        Value = value;
        Failure = failure;
    }

    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Failure))]
    public bool IsSuccess => Failure is null;

    public T? Value { get; }

    public InventoryFailure? Failure { get; }

    public static Result<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Result<T>(value, null);
    }

    public static Result<T> Fail(InventoryFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new Result<T>(default, failure);
    }

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(InventoryFailure failure) => Fail(failure);
}
