using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Identity;
using Percolator.Identity.Model;

namespace Desktop.Wpf.Features.Identity;

public sealed class InMemoryPeerIdentityRepository : IPeerIdentityRepository
{
    private readonly Dictionary<Guid, PeerIdentity> _byId = new();

    public InMemoryPeerIdentityRepository()
    {
        // Seed a couple of peers that match the InMemorySessionRepository remote ids
        var now = DateTimeOffset.UtcNow;
        var p1 = new PeerIdentity(new PeerId(Guid.Parse("11111111-1111-1111-1111-111111111111")));
        p1.SetDisplayName("Ada Lovelace");
        // No keys needed for UI display/testing
        var p2 = new PeerIdentity(new PeerId(Guid.Parse("22222222-2222-2222-2222-222222222222")));
        p2.SetDisplayName("Alan Turing");
        _byId[p1.Id.Value] = p1;
        _byId[p2.Id.Value] = p2;
    }

    public Task<PeerIdentity?> GetByIdAsync(PeerId id, CancellationToken ct = default)
        => Task.FromResult(_byId.TryGetValue(id.Value, out var p) ? p : null);

    public Task<PeerIdentity?> GetByNameAsync(DisplayName name, CancellationToken ct = default)
        => Task.FromResult(_byId.Values.FirstOrDefault(p => p.DisplayName?.Value.Equals(name.Value, StringComparison.OrdinalIgnoreCase) == true));

    public Task<PeerIdentity?> FindByPublicKeyHashAsync(byte[] fingerprint, CancellationToken ct = default)
        => Task.FromResult<PeerIdentity?>(null);

    public Task SaveAsync(PeerIdentity peer, CancellationToken ct = default)
    {
        _byId[peer.Id.Value] = peer;
        return Task.CompletedTask;
    }
}
