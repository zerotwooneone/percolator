using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;

namespace Percolator.Domain.Security.Ports;

/// <summary>
/// Port for persisting and retrieving outbound group sender key ratchets and inbound group receiver sessions.
/// </summary>
public interface IGroupRatchetSessionRepository
{
    Task<GroupSenderKeyRatchet?> GetSenderRatchetAsync(
        ChannelId channelId,
        PublicIdentityId authorId,
        DeviceId authorDeviceId,
        CancellationToken cancellationToken = default);

    Task<GroupReceiverSession?> GetReceiverSessionAsync(
        ChannelId channelId,
        PublicIdentityId authorId,
        DeviceId authorDeviceId,
        uint keyId,
        CancellationToken cancellationToken = default);

    Task SaveSenderRatchetAsync(GroupSenderKeyRatchet ratchet, CancellationToken cancellationToken = default);

    Task SaveReceiverSessionAsync(GroupReceiverSession session, CancellationToken cancellationToken = default);

    Task DeleteChannelSessionsAsync(ChannelId channelId, CancellationToken cancellationToken = default);
}
