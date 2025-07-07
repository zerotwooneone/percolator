namespace Percolator.Cryptography;

public record MessageKey(byte[] Value) : ByteArrayRecord(Value);
