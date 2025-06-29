namespace Percolator.Identity;

public interface IPeerRepository
{
    Task<Peer?> GetByIdAsync(PeerId peerId);
    Task<Peer?> GetByThumbprintAsync(string thumbprint);
    Task AddAsync(Peer peer);
    Task RemoveAsync(PeerId peerId);
}
