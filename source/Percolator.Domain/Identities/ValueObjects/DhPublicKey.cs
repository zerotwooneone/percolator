using Percolator.SourceGenerators;

namespace Percolator.Domain.Identities.ValueObjects;

/// <summary>
/// Curve25519 (X25519) Diffie-Hellman public key used for pre-keys (SPK, OPK) and ephemeral session exchanges (EK).
/// </summary>
[ByteArray(length: 32)]
public sealed partial record DhPublicKey;
