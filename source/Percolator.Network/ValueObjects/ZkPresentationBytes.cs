using Percolator.SourceGenerators;

namespace Percolator.Network.ValueObjects;

/// <summary>
/// Zero-knowledge proof presentation bytes for membership or admin authorization.
/// </summary>
[ByteArray(minLength: 1, maxLength: 1000)]
public sealed partial record ZkPresentationBytes;
