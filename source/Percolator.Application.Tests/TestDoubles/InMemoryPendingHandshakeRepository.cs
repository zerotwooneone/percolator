using Percolator.Application.Ingress;
using Percolator.Application.Ports;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Tests.TestDoubles;

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
