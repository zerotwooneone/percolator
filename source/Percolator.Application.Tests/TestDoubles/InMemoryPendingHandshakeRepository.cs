using Percolator.Application2.Ingress;
using Percolator.Application2.Ports;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Tests.TestDoubles;

public sealed class InMemoryPendingHandshakeRepository : IPendingHandshakeRepository
{
    private readonly Dictionary<(PublicIdentityId Recipient, PublicIdentityId Sender), InboundHandshakeEnvelope> _pending = [];

    public Task SavePendingHandshakeAsync(PublicIdentityId recipientId, PublicIdentityId senderId, InboundHandshakeEnvelope envelope, CancellationToken ct = default)
    {
        _pending[(recipientId, senderId)] = envelope;
        return Task.CompletedTask;
    }

    public Task<InboundHandshakeEnvelope?> GetPendingHandshakeAsync(PublicIdentityId recipientId, PublicIdentityId senderId, CancellationToken ct = default)
    {
        _pending.TryGetValue((recipientId, senderId), out var env);
        return Task.FromResult(env);
    }

    public Task DeletePendingHandshakeAsync(PublicIdentityId recipientId, PublicIdentityId senderId, CancellationToken ct = default)
    {
        _pending.Remove((recipientId, senderId));
        return Task.CompletedTask;
    }
}
