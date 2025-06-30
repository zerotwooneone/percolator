using System.Collections.Concurrent;
using Percolator.Identity;

namespace Percolator.Application.Identity;

public class InMemoryPeerRepository : IPeerRepository
{
    private readonly ConcurrentDictionary<Guid, Peer> _peersById = new();
    private readonly ConcurrentDictionary<string, Peer> _peersByThumbprint = new();

    public Task<Peer?> GetByIdAsync(PeerId peerId)
    {
        _peersById.TryGetValue(peerId.Value, out var peer);
        return Task.FromResult(peer);
    }

    public Task<Peer?> GetByThumbprintAsync(string thumbprint)
    {
        _peersByThumbprint.TryGetValue(thumbprint, out var peer);
        return Task.FromResult(peer);
    }

    public Task AddAsync(Peer peer)
    {
        _peersById.TryAdd(peer.Id.Value, peer);
        _peersByThumbprint.TryAdd(peer.Thumbprint, peer);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(PeerId peerId)
    {
        if (_peersById.TryRemove(peerId.Value, out var peer))
        {
            _peersByThumbprint.TryRemove(peer.Thumbprint, out _);
        }
        return Task.CompletedTask;
    }
}
