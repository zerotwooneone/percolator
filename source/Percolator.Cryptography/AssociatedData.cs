using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public record AssociatedData(byte[] Value) : ByteArrayRecord(Value);
