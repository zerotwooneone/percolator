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
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public T Value { get; }
    public DomainError Error { get; }

    private DomainResult(bool isSuccess, T value, DomainError error)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    public static DomainResult<T> Success(T value) => new(true, value, DomainError.None);
    public static DomainResult<T> Failure(DomainError error) => new(false, default!, error);

    public static implicit operator DomainResult<T>(T value) => Success(value);
}
