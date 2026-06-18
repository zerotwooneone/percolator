using Percolator.SourceGenerators;

namespace Percolator.Cryptography;

/// <summary>
/// Represents a Zero-Knowledge presentation for group authentication.
/// Variable length depending on the presentation type.
/// </summary>
[ByteArray(minLength: 1, maxLength: 1024)]
public sealed partial record ZkPresentationBytes;
