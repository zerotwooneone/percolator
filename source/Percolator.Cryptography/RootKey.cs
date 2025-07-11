using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public record RootKey(byte[] Value) : ByteArrayRecord(Value);
