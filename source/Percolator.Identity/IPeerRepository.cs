namespace Percolator.Identity;

public interface IPeerRepository
{
    Task<Peer?> GetByIdAsync(Guid peerId);
    Task<Peer?> GetByThumbprintAsync(string thumbprint);
}
