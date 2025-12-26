namespace Percolator.Cryptography.Primitives;

public readonly record struct RequestCorrelationId
{
    public Guid Value { get; }

    public RequestCorrelationId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("request correlation id must not be empty", nameof(value));

        Value = value;
    }

    public override string ToString() => Value.ToString("D");
}
