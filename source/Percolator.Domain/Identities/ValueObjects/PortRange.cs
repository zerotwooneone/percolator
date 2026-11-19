namespace Percolator.Domain.Identities.ValueObjects;

using System;

public sealed record PortRange
{
    public int MinPort { get; }
    public int MaxPort { get; }

    public PortRange(int minPort, int maxPort)
    {
        if (minPort < 1 || minPort > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(minPort), minPort, "Port out of range.");
        }

        if (maxPort < 1 || maxPort > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPort), maxPort, "Port out of range.");
        }

        if (minPort > maxPort)
        {
            throw new ArgumentException("MinPort must be less than or equal to MaxPort.", nameof(minPort));
        }

        MinPort = minPort;
        MaxPort = maxPort;
    }

    public bool IsSinglePort => MinPort == MaxPort;

    public bool Contains(int port) => port >= MinPort && port <= MaxPort;

    public static PortRange DefaultDynamicRange => new(5100, 5199);

    public static PortRange Single(int port) => new(port, port);

    public override string ToString() => IsSinglePort ? MinPort.ToString() : $"{MinPort}-{MaxPort}";
}
