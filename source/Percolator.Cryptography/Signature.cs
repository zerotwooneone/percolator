using Percolator.SourceGenerators;

namespace Percolator.Cryptography;

[ByteArray(minLength: 60, maxLength: 120)]
public sealed partial record Signature;
