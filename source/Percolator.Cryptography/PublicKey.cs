using Percolator.SourceGenerators;

namespace Percolator.Cryptography;

[ByteArray(minLength: 80, maxLength: 200)]
public sealed partial record PublicKey;