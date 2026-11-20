namespace Percolator.Application2IntegrationTests.Infrastructure;

public readonly record struct EndpointPort
{
    public int Value { get; }

    public EndpointPort(int value)
    {
        if (value is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Port must be between 1 and 65535.");
        }

        Value = value;
    }

    public static implicit operator int(EndpointPort port) => port.Value;
    public static implicit operator EndpointPort(int value) => new(value);
    public override string ToString() => Value.ToString();
}
