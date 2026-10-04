using Percolator.Application2.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;

namespace Percolator.Application2.Tests.TestDoubles;

public sealed class InMemoryGroupReceiverSessionRepository : IGroupReceiverSessionRepository
{
    private readonly Dictionary<(ChannelId, PublicIdentityId, DeviceId), GroupReceiverSession> _sessions = [];

    public Task<GroupReceiverSession?> GetReceiverSessionAsync(
        ChannelId channelId,
        PublicIdentityId authorId,
        DeviceId authorDeviceId,
        CancellationToken ct = default)
    {
        _sessions.TryGetValue((channelId, authorId, authorDeviceId), out var session);
        return Task.FromResult(session);
    }

    public Task SaveReceiverSessionAsync(
        GroupReceiverSession session,
        CancellationToken ct = default)
    {
        _sessions[(session.ChannelId, session.AuthorId, session.AuthorDeviceId)] = session;
        return Task.CompletedTask;
    }
}
