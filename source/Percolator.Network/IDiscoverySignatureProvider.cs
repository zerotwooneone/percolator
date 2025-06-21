namespace Percolator.Network;

public interface IDiscoverySignatureProvider
{
    byte[] GetPublicKeyCertificate();

    string GetThumbprint(byte[] publicKeyCertificate);

    byte[] Sign(byte[] data);

    bool Verify(byte[] data, byte[] signature, byte[] publicKeyCertificate);
}
