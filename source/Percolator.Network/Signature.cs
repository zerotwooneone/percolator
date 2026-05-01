using Percolator.SourceGenerators;

namespace Percolator.Network;

/// <summary>
/// A DDD value type representing a cryptographic signature.
/// </summary>
[ByteArray(minLength: 60, maxLength: 120)]
public sealed partial record Signature;
