using Percolator.SourceGenerators;

namespace Percolator.Cryptography;

[ByteArray(minLength: 100, maxLength: 250)]
public sealed partial record PrivateEphemeralKey;