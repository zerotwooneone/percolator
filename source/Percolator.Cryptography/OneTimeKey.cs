using Percolator.Cryptography.Primitives;
using Percolator.SourceGenerators;

namespace Percolator.Cryptography;

[ByteArray(minLength: 64, maxLength: 200)]
public sealed partial record OneTimeKey;