using Percolator.SourceGenerators;

namespace Percolator.Network;

/// <summary>
/// A DDD value type representing a generic payload of bytes.
/// </summary>
[ByteArray(minLength: 1, maxLength: 1000000)]
public sealed partial record Payload;
