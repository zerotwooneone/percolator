namespace Percolator.Application.Network;

public enum SimulatorOutboundMode
{
    Mirror = 0,
    SimulateOnly = 1
}

public sealed class SimulatorWireTapOptions
{
    public const string SectionName = "SimulatorWireTap";

    public bool Enabled { get; set; } = false;

    public SimulatorOutboundMode Mode { get; set; } = SimulatorOutboundMode.Mirror;
}
