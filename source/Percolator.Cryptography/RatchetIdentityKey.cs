using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public record RatchetIdentityKey(byte[] Value) : ByteArrayRecord(Value);