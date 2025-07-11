using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public record PrivatePreKey(byte[] Value) : ByteArrayRecord(Value);