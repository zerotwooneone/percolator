using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Identities.ValueObjects;

/// <summary>
/// Immutable value object containing the public key material published by an identity device
/// for establishing an asynchronous Double Ratchet session (e.g. via X3DH).
/// </summary>
public sealed record PreKeyBundle(
    PublicIdentityId IdentityId,
    DeviceId DeviceId,
    IdentityPublicKey IdentityKey,
    IdentityPublicKey SignedPreKey,
    DeviceLinkProof SignedPreKeySignature,
    IdentityPublicKey? OneTimePreKey = null,
    uint OneTimePreKeyId = 0);
