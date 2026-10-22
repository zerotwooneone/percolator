using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Security.ValueObjects;

/// <summary>
/// Cryptographic header transmitted on the wire alongside the initial encrypted payload during an X3DH handshake.
/// </summary>
public sealed record HandshakeHeader(
    IdentityKey SenderIdentityKey,
    DhPublicKey SenderEphemeralKey,
    uint SignedPreKeyId,
    uint? OneTimePreKeyId);
