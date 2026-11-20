using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application.Delivery.Ports;

/// <summary>
/// Relay-side hosted pre-key bundle snapshot for maintenance decisions.
/// <paramref name="Found"/> is false when no hosted bundle exists for the routing
/// token + device (distinct from a found bundle that simply has 0 OPKs).
/// </summary>
public sealed record RelayPreKeyStatus(
    bool Found,
    uint OneTimePreKeyCount,
    SignedPreKeyId SignedPreKeyId);
