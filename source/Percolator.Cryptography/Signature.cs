namespace Percolator.Cryptography;

public record Signature(byte[] Value) : ByteArrayRecord(Value);
