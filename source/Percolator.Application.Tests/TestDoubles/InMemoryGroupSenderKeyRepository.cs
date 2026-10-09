using Percolator.Application2.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;

namespace Percolator.Application2.Tests.TestDoubles;

public sealed class InMemoryGroupSenderKeyRepository : IGroupSenderKeyRepository
{
    private readonly Dictionary<(ChannelId, PublicIdentityId, DeviceId), GroupSenderKeyRatchet> _ratchets = [];

    public Task<GroupSenderKeyRatchet?> GetSenderKeyRatchetAsync(
        ChannelId channelId,
        PublicIdentityId authorId,
        DeviceId authorDeviceId,
        CancellationToken ct = default)
    {
        _ratchets.TryGetValue((channelId, authorId, authorDeviceId), out var ratchet);
        return Task.FromResult(ratchet);
    }

    public Task SaveSenderKeyRatchetAsync(
        GroupSenderKeyRatchet ratchet,
        CancellationToken ct = default)
    {
        _ratchets[(ratchet.ChannelId, ratchet.AuthorId, ratchet.AuthorDeviceId)] = ratchet;
        return Task.CompletedTask;
    }
}
