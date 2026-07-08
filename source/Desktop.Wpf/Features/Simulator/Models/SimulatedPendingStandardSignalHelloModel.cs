using Percolator.Identity;
using Percolator.Network;

namespace Desktop.Wpf.Features.Simulator.Models;

public sealed record SimulatedPendingStandardSignalHelloModel(
    NetworkPeerId RelayHostNetworkPeerId,
    PublicIdentityId PublicIdentityId,
    byte[] InitiatorIdentityKeySpki,
    byte[] InitiatorEphemeralKeySpki,
    Guid SignedPreKeyId,
    Guid? OneTimePreKeyId,
    DateTimeOffset ReceivedUtc);
