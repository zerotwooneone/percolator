using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public record PrivateOneTimeKey(byte[] Value) : ByteArrayRecord(Value);