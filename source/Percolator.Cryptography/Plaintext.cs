using Percolator.Cryptography.Primitives;
using Percolator.SourceGenerators;

namespace Percolator.Cryptography;

[ByteArray(minLength: 1, maxLength: 1000000)]
public sealed partial record Plaintext
{
    public static Plaintext Empty { get; } = new(Array.Empty<byte>());
}
