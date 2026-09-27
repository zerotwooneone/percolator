namespace Percolator.Domain.Common;

public readonly record struct DomainResult
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public DomainError Error { get; }

    private DomainResult(bool isSuccess, DomainError error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public static DomainResult Success() => new(true, DomainError.None);
    public static DomainResult Failure(DomainError error) => new(false, error);
}

public readonly record struct DomainResult<T>
{
    private readonly T _value;

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public DomainError Error { get; }

    public T Value => IsSuccess
        ? _value
        : throw new InvalidOperationException($"Cannot access Value of failed result ({Error.Code}: {Error.Description}).");

    private DomainResult(bool isSuccess, T value, DomainError error)
    {
        IsSuccess = isSuccess;
        _value = value;
        Error = error;
    }

    public static DomainResult<T> Success(T value) => new(true, value, DomainError.None);
    public static DomainResult<T> Failure(DomainError error) => new(false, default!, error);

    public static implicit operator DomainResult<T>(T value) => Success(value);
}
