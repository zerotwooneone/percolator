namespace Percolator.Network;

public interface ITrustedPeerStore
{
    void Add(PublicKeyHash publicKeyHash);
    bool IsTrusted(PublicKeyHash publicKeyHash);
}