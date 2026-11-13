using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Identities.ValueObjects;

/// <summary>
/// Immutable value object containing the public key material published by an identity device
/// for establishing an asynchronous Double Ratchet session (e.g. via X3DH).
/// Carries no owner identity: the requesting party already knows which peer's bundle it asked
/// for (mirroring libsignal, where the peer address is supplied to the session builder separately).
/// </summary>
public sealed record PreKeyBundle(
    DeviceId DeviceId,
    IdentityKey IdentityKey,
    DhPublicKey SignedPreKey,
    DeviceLinkProof SignedPreKeySignature,
    DhPublicKey? OneTimePreKey = null,
    uint OneTimePreKeyId = 0);
