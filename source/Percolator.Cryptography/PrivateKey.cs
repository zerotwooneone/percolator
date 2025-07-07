namespace Percolator.Cryptography;

public record PrivateKey(byte[] Value) : ByteArrayRecord(Value);
