using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Identity.Model;

namespace Percolator.Identity;

public sealed class InMemoryPeerIdentityRepository : IPeerIdentityRepository
{
    private readonly ConcurrentDictionary<Guid, PeerIdentity> _byId = new();
    private readonly ConcurrentDictionary<string, Guid> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Guid> _byFingerprint = new(StringComparer.Ordinal);

    public Task<PeerIdentity?> GetByIdAsync(PeerId id, CancellationToken ct = default)
    {
        _byId.TryGetValue(id.Value, out var value);
        return Task.FromResult<PeerIdentity?>(value);
    }

    public Task<PeerIdentity?> GetByNameAsync(DisplayName name, CancellationToken ct = default)
    {
        if (_byName.TryGetValue(name.Value, out var id) && _byId.TryGetValue(id, out var value))
            return Task.FromResult<PeerIdentity?>(value);
        return Task.FromResult<PeerIdentity?>(null);
    }

    public Task<PeerIdentity?> FindByPublicKeyHashAsync(byte[] fingerprint, CancellationToken ct = default)
    {
        var key = Convert.ToBase64String(fingerprint);
        if (_byFingerprint.TryGetValue(key, out var id) && _byId.TryGetValue(id, out var value))
            return Task.FromResult<PeerIdentity?>(value);
        return Task.FromResult<PeerIdentity?>(null);
    }

    public Task SaveAsync(PeerIdentity peer, CancellationToken ct = default)
    {
        // Optimistic concurrency: compare existing version
        _byId.AddOrUpdate(peer.Id.Value,
            addValueFactory: _ =>
            {
                peer.SetVersionForTesting(1);
                Index(peer);
                return peer;
            },
            updateValueFactory: (_, existing) =>
            {
                if (peer.Version != existing.Version)
                    throw new InvalidOperationException("Concurrency conflict saving PeerIdentity.");
                peer.SetVersionForTesting(existing.Version + 1);
                // Update indices if name or active key changed
                Index(peer);
                return peer;
            });
        return Task.CompletedTask;
    }

    private void Index(PeerIdentity peer)
    {
        if (peer.DisplayName != null)
            _byName[peer.DisplayName.Value] = peer.Id.Value;
        var active = peer.GetActiveKey(DateTimeOffset.UtcNow);
        if (active != null)
        {
            var key = Convert.ToBase64String(active.Fingerprint);
            _byFingerprint[key] = peer.Id.Value;
        }
    }
}
