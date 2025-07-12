namespace Percolator.Network;

public interface ITrustedPeerStore
{
    Task AddAsync(PublicKeyHash publicKeyHash);
    bool IsTrusted(PublicKeyHash publicKeyHash);
    Task<IEnumerable<PublicKeyHash>> GetAllAsync();
}