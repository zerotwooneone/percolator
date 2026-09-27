using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Security.ValueObjects;

/// <summary>
/// Cryptographic header transmitted alongside encrypted Double Ratchet ciphertext.
/// Contains the ephemeral public key and counter state required by the receiver to step the ratchet.
/// </summary>
public readonly record struct RatchetHeader(
    IdentityPublicKey EphemeralPublicKey,
    uint Counter,
    uint PreviousChainLength);
