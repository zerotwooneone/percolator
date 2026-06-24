using Percolator.SourceGenerators;

namespace Percolator.Cryptography;

[ByteArray(minLength: 1, maxLength: 5000)]
public sealed partial record ZkPresentationBytes;
