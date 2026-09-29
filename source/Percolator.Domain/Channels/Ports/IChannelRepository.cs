using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Channels.Ports;

public interface IChannelRepository
{
    Task<DirectChannel?> GetDirectByIdAsync(ChannelId id, CancellationToken cancellationToken = default);
    Task<DirectChannel?> GetDirectByPeerAsync(PublicIdentityId ownerId, PublicIdentityId remotePeerId, CancellationToken cancellationToken = default);
    Task<GroupChannel?> GetGroupByIdAsync(ChannelId id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GroupChannel>> GetAllGroupsForOwnerAsync(PublicIdentityId ownerId, CancellationToken cancellationToken = default);
    Task SaveDirectAsync(DirectChannel channel, CancellationToken cancellationToken = default);
    Task SaveGroupAsync(GroupChannel channel, CancellationToken cancellationToken = default);
}
