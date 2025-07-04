namespace Percolator.Cryptography;

public record SharedSecret(byte[] Value) : ByteArrayRecord(Value);
