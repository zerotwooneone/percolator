namespace Percolator.Grpc.Services;

public struct Handshake(byte[] iv, byte[] encryptedSessionKey)
{
    public byte[] Iv { get; init; } = iv;
    public byte[] EncryptedSessionKey { get; init; } = encryptedSessionKey;
}