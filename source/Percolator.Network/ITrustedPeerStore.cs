namespace Percolator.Network;

public interface ITrustedPeerStore
{
    Task AddAsync(PublicKeyHash publicKeyHash);
    bool IsTrusted(PublicKeyHash publicKeyHash);
}