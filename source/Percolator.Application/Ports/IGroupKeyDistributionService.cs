using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Ports;

/// <summary>
/// Coordinates distributing an author's GroupSenderKey to group members via pairwise 1:1 Double Ratchet channels,
/// and processes inbound sender key distribution messages to instantiate GroupReceiverSessions.
/// </summary>
public interface IGroupKeyDistributionService
{
    ValueTask<DomainResult> DistributeSenderKeyAsync(
        ChannelId channelId,
        PublicIdentityId senderId,
        DeviceId senderDeviceId,
        IEnumerable<PublicIdentityId> recipientMemberIds,
        CancellationToken ct = default);

    ValueTask<DomainResult> ProcessInboundDistributionAsync(
        InboundPayloadContext context,
        CancellationToken ct = default);
}
