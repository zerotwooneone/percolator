using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;

namespace Percolator.Application2.Ports;

public interface IGroupReceiverSessionRepository
{
    Task<GroupReceiverSession?> GetReceiverSessionAsync(
        ChannelId channelId,
        PublicIdentityId authorId,
        DeviceId authorDeviceId,
        CancellationToken ct = default);

    Task SaveReceiverSessionAsync(
        GroupReceiverSession session,
        CancellationToken ct = default);
}
