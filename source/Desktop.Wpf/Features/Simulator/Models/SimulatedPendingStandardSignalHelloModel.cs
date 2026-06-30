using Percolator.Identity;
using PeerId = Percolator.Network.PeerId;

namespace Desktop.Wpf.Features.Simulator.Models;

public sealed record SimulatedPendingStandardSignalHelloModel(
    PeerId RelayHostPeerId,
    PublicIdentityId PublicIdentityId,
    byte[] InitiatorIdentityKeySpki,
    byte[] InitiatorEphemeralKeySpki,
    Guid SignedPreKeyId,
    Guid? OneTimePreKeyId,
    DateTimeOffset ReceivedUtc);
