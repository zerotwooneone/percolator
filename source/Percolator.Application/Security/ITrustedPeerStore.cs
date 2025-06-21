namespace Percolator.Application.Security
{
    public interface ITrustedPeerStore
    {
        void Add(string thumbprint);
        bool IsTrusted(string thumbprint);
    }
}
