namespace Percolator.Identity;

public interface IPeerRepository
{
    Task<Peer?> GetByIdAsync(PeerId peerId);
    Task<Peer?> GetByThumbprintAsync(string thumbprint);
}
