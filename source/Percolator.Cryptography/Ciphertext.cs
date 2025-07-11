using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public record Ciphertext(byte[] Value) : ByteArrayRecord(Value);
