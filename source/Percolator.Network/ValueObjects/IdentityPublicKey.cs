using Percolator.SourceGenerators;

namespace Percolator.Network.ValueObjects;

[ByteArray(minLength: 80, maxLength: 200)]
public sealed partial record IdentityPublicKey;
