using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public record Plaintext(byte[] Value) : ByteArrayRecord(Value);
