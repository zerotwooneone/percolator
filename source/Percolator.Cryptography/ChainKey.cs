using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public record ChainKey(byte[] Value) : ByteArrayRecord(Value);
