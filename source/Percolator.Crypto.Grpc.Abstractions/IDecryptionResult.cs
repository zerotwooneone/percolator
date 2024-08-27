namespace Percolator.Crypto.Grpc.Abstractions;

public interface IDecryptionResult
{
    byte[] Decrypted { get; }
    byte[] AssociatedData { get; }
}