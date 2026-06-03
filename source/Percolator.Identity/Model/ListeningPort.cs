namespace Percolator.Identity.Model;

public sealed record ListeningPort
{
    public const int MinPort = 1024;
    public const int MaxPort = 65535;
    public int Value { get; }

    public ListeningPort(int value)
    {
        if (value < MinPort)
            throw new ArgumentOutOfRangeException($"Port must be at least {MinPort} (non-privileged ports only).", nameof(value));
        if (value > MaxPort)
            throw new ArgumentOutOfRangeException($"Port cannot exceed {MaxPort}.", nameof(value));

        Value = value;
    }

    public override string ToString() => Value.ToString();
}
