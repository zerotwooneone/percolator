namespace Percolator.Simulator.Features.Topology;

/// <summary>
/// Target mode for establishing a peer-to-peer connection from a simulated persona.
/// </summary>
public enum PeerConnectionTargetKind
{
    /// <summary>Connects to another active persona in the local simulation world.</summary>
    InWorldSimulatedNode = 0,

    /// <summary>Connects to an external node via out-of-band invitation URL or code.</summary>
    ExternalInvitationCode = 1
}
