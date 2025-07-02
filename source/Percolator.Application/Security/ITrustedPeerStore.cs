using Percolator.Network;

namespace Percolator.Application.Security
{
    public interface ITrustedPeerStore
    {
        void Add(PublicKeyHash publicKeyHash);
        bool IsTrusted(PublicKeyHash publicKeyHash);
    }
}
