using Percolator.Network;

namespace Desktop.Wpf.Features.Simulator.Models;

public sealed record SimulatedPendingStandardSignalHelloModel(
    PeerId RelayHostPeerId,
    byte[] InitiatorIdentityKeySpki,
    byte[] InitiatorEphemeralKeySpki,
    Guid SignedPreKeyId,
    Guid? OneTimePreKeyId,
    DateTimeOffset ReceivedUtc);
