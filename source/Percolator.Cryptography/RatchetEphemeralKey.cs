using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public record RatchetEphemeralKey(byte[] Value) : ByteArrayRecord(Value);