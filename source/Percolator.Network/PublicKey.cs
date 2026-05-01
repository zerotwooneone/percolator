using Percolator.SourceGenerators;

namespace Percolator.Network;

/// <summary>
/// A DDD value type representing a public key.
/// </summary>
[ByteArray(minLength: 80, maxLength: 200)]
public sealed partial record PublicKey;
