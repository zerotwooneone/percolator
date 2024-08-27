namespace Percolator.Crypto.Grpc.Abstractions;

public interface IEncryptionResult
{
    byte[] Encrypted { get; }
    byte[] Header { get; }
    byte[] HeaderSignature { get; }
}