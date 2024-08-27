namespace Percolator.Crypto.Grpc;

public class DecryptResult
{
    public byte[] PlainText { get; init; }
    public byte[] AssociatedData { get; init; }
    public uint MessageNumber { get; init; }
}