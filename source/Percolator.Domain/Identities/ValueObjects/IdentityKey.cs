using Percolator.SourceGenerators;

namespace Percolator.Domain.Identities.ValueObjects;

/// <summary>
/// Long-term Ed25519 public identity key (IK) representing an entity's cryptographic identity and signing authority.
/// </summary>
[ByteArray(length: 32)]
public sealed partial record IdentityKey;
