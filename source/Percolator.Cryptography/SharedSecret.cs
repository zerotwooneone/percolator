using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public record SharedSecret(byte[] Value) : ByteArrayRecord(Value);
