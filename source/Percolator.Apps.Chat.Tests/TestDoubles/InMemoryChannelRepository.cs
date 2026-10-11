using System.Collections.Concurrent;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.Chat.Tests.TestDoubles;

public sealed class InMemoryChannelRepository : IChannelRepository
{
    private readonly ConcurrentDictionary<ChannelId, DirectChannel> _directChannels = new();
    private readonly ConcurrentDictionary<ChannelId, GroupChannel> _groupChannels = new();

    public Task<DirectChannel?> GetDirectByIdAsync(ChannelId id, CancellationToken cancellationToken = default)
    {
        _directChannels.TryGetValue(id, out var channel);
        return Task.FromResult(channel);
    }

    public Task<DirectChannel?> GetDirectByPeerAsync(PublicIdentityId ownerId, PublicIdentityId remotePeerId, CancellationToken cancellationToken = default)
    {
        var channel = _directChannels.Values.FirstOrDefault(c =>
            (c.OwnerIdentityId == ownerId && c.RemotePeerId == remotePeerId) ||
            (c.OwnerIdentityId == remotePeerId && c.RemotePeerId == ownerId));
        return Task.FromResult(channel);
    }

    public Task<GroupChannel?> GetGroupByIdAsync(ChannelId id, CancellationToken cancellationToken = default)
    {
        _groupChannels.TryGetValue(id, out var channel);
        return Task.FromResult(channel);
    }

    public Task<IReadOnlyList<GroupChannel>> GetAllGroupsForOwnerAsync(PublicIdentityId ownerId, CancellationToken cancellationToken = default)
    {
        var list = _groupChannels.Values
            .Where(g => g.Members.Any(m => m.Id == ownerId))
            .ToList();
        return Task.FromResult<IReadOnlyList<GroupChannel>>(list);
    }

    public Task SaveDirectAsync(DirectChannel channel, CancellationToken cancellationToken = default)
    {
        _directChannels[channel.Id] = channel;
        return Task.CompletedTask;
    }

    public Task SaveGroupAsync(GroupChannel channel, CancellationToken cancellationToken = default)
    {
        _groupChannels[channel.Id] = channel;
        return Task.CompletedTask;
    }
}
