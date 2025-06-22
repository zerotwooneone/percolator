using Percolator.Identity;
using System.Collections.Concurrent;

namespace Percolator.Application.Identity;

public class InMemoryPeerIdentityStore : IPeerIdentityStore
{
    private readonly ConcurrentDictionary<string, PeerIdentity> _peers = new();

    public Task StorePeerAsync(PeerIdentity peer)
    {
        var key = Convert.ToBase64String(peer.IdentityKey);
        _peers[key] = peer;
        return Task.CompletedTask;
    }

    public Task<PeerIdentity?> GetPeerAsync(byte[] identityKey)
    {
        var key = Convert.ToBase64String(identityKey);
        _peers.TryGetValue(key, out var peer);
        return Task.FromResult(peer);
    }
}
