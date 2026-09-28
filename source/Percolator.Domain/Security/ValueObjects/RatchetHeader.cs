using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Security.ValueObjects;

/// <summary>
/// Cryptographic header transmitted alongside encrypted Double Ratchet ciphertext.
/// Contains the ephemeral DH public key and counter state required by the receiver to step the ratchet.
/// </summary>
public readonly record struct RatchetHeader(
    DhPublicKey EphemeralPublicKey,
    uint Counter,
    uint PreviousChainLength);
