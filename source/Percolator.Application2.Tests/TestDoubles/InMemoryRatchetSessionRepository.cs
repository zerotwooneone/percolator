using Percolator.Application2.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;

namespace Percolator.Application2.Tests.TestDoubles;

public sealed class InMemoryRatchetSessionRepository : IRatchetSessionRepository
{
    private readonly Dictionary<(PublicIdentityId, PublicIdentityId, DeviceId), DirectRatchetSession> _sessions = [];

    public Task<DirectRatchetSession?> GetSessionAsync(
        PublicIdentityId ownerId,
        PublicIdentityId remotePeerId,
        DeviceId remoteDeviceId,
        CancellationToken ct = default)
    {
        _sessions.TryGetValue((ownerId, remotePeerId, remoteDeviceId), out var session);
        return Task.FromResult(session);
    }

    public Task SaveSessionAsync(DirectRatchetSession session, CancellationToken ct = default)
    {
        _sessions[(session.OwnerIdentityId, session.RemotePeerId, session.RemoteDeviceId)] = session;
        return Task.CompletedTask;
    }
}
