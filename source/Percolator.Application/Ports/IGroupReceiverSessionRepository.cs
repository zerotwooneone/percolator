using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Ports;

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
