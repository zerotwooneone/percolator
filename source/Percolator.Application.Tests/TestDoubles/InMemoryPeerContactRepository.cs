using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Tests.TestDoubles;

public sealed class InMemoryPeerContactRepository : IPeerContactRepository
{
    private readonly Dictionary<(PublicIdentityId Owner, PublicIdentityId Peer), PeerContact> _contacts = [];

    public Task<PeerContact?> GetByPeerIdAsync(PublicIdentityId ownerId, PublicIdentityId remotePeerId, CancellationToken cancellationToken = default)
    {
        _contacts.TryGetValue((ownerId, remotePeerId), out var contact);
        return Task.FromResult(contact);
    }

    public Task<IReadOnlyList<PeerContact>> GetAllForOwnerAsync(PublicIdentityId ownerId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PeerContact> list = _contacts.Values.Where(c => c.OwnerIdentityId == ownerId).ToList();
        return Task.FromResult(list);
    }

    public Task SaveAsync(PeerContact contact, CancellationToken cancellationToken = default)
    {
        _contacts[(contact.OwnerIdentityId, contact.RemotePeerId)] = contact;
        return Task.CompletedTask;
    }
}
