namespace Percolator.Domain.Common;

public readonly record struct DomainError(string Code, string Description)
{
    public static readonly DomainError None = default;

    public bool IsEmpty => string.IsNullOrEmpty(Code);
}
