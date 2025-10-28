namespace Percolator.Identity;

public interface IPeerRepository
{
    Task<Peer?> GetByIdAsync(PeerId peerId);
    Task AddAsync(Peer peer);
    Task AddOrUpdateAsync(Peer peer);
    Task RemoveAsync(PeerId peerId);
    Task<Peer?> GetByNameAsync(string name);
}
