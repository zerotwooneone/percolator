using Percolator.Sessions;

namespace Percolator.Application.Sessions;

public interface ILocalPeerProvider
{
    Task<PeerId> GetPeerIdAsync();
}
