using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public record PreKey(byte[] Value) : ByteArrayRecord(Value);