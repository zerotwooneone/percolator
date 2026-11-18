namespace Percolator.Simulator.Features.Topology;

/// <summary>
/// Controls how simulated persona and relay endpoints are advertised to peer and external clients.
/// </summary>
public enum SimulatorAdvertisedHostKind
{
    /// <summary>Auto-detects the machine's primary local IPv4 LAN address.</summary>
    AutoLanIp = 0,

    /// <summary>Forces loopback 127.0.0.1 for local single-machine simulation.</summary>
    Loopback = 1,

    /// <summary>Allows entering a custom routable hostname or IP override.</summary>
    Custom = 2
}
