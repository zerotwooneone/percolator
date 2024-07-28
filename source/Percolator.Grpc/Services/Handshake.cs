namespace Percolator.Grpc.Services;

public struct Handshake(System.Security.Cryptography.Aes key, byte[] encryptedSessionKey)
{
    public System.Security.Cryptography.Aes Key { get; init; } = key;
    public byte[] EncryptedSessionKey { get; init; } = encryptedSessionKey;
}