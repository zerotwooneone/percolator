using Percolator.Identity;
using System.Collections.Concurrent;

namespace Percolator.Application.Identity;

public class PeerRepository : IPeerRepository
{
    private readonly ConcurrentDictionary<Guid, Peer> _peers = new();

    public Task<Peer?> GetByIdAsync(Guid id)
    {
        _peers.TryGetValue(id, out var peer);
        return Task.FromResult(peer);
    }

    public Task<Peer?> GetByThumbprintAsync(string thumbprint)
    {
        var peer = _peers.Values.FirstOrDefault(p => p.Thumbprint == thumbprint);
        return Task.FromResult(peer);
    }

    public Task AddAsync(Peer peer)
    {
        _peers[peer.Id] = peer;
        return Task.CompletedTask;
    }
}
