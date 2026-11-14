using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Ports;

public interface IGroupSenderKeyRepository
{
    Task<GroupSenderKeyRatchet?> GetSenderKeyRatchetAsync(
        ChannelId channelId,
        PublicIdentityId authorId,
        DeviceId authorDeviceId,
        CancellationToken ct = default);

    Task SaveSenderKeyRatchetAsync(
        GroupSenderKeyRatchet ratchet,
        CancellationToken ct = default);
}
