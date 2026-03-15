namespace Percolator.Identity;

public readonly record struct SelfId
{
    public int Value { get; }

    public SelfId(int value)
    {
        if (value < 0)
            throw new ArgumentException("SelfId cannot be negative.", nameof(value));
        Value = value;
    }

    public override string ToString() => Value.ToString();
}
