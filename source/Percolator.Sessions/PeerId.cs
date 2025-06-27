namespace Percolator.Sessions;

public record PeerId
{
    public Guid Value { get; }

    public PeerId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("Peer ID cannot be empty.", nameof(value));
        Value = value;
    }

    public static PeerId NewId() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
