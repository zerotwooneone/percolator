using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public record PrivateEphemeralKey(byte[] Value) : ByteArrayRecord(Value);