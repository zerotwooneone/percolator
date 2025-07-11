using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public record OneTimeKey(byte[] Value) : ByteArrayRecord(Value);