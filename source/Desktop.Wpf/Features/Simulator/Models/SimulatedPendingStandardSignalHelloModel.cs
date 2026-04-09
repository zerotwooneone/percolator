namespace Desktop.Wpf.Features.Simulator.Models;

public sealed record SimulatedPendingStandardSignalHelloModel(
    Guid RelayHostPeerId,
    byte[] InitiatorIdentityKeySpki,
    byte[] InitiatorEphemeralKeySpki,
    Guid SignedPreKeyId,
    Guid? OneTimePreKeyId,
    DateTimeOffset ReceivedUtc);
