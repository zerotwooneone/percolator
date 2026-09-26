using Percolator.SourceGenerators;

namespace Percolator.Domain.Identities.ValueObjects;

[ByteArray(length: 32)]
public sealed partial record IdentityPublicKey;
