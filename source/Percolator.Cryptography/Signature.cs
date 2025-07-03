using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public record Signature(byte[] Value) : ByteArrayRecord(Value);
