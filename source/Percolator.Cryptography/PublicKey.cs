using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public record PublicKey(byte[] Value) : ByteArrayRecord(Value);
