using Percolator.SourceGenerators;

namespace Percolator.Cryptography;

[ByteArray(minLength: 32, maxLength: 200)]
public sealed partial record SignaturePublicKey;
