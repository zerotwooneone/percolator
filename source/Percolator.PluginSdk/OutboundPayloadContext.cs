using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.PluginSdk;

public readonly record struct OutboundPayloadContext(
    ChannelId ChannelId,
    PublicIdentityId SenderIdentityId,
    PublicIdentityId? RecipientIdentityId,
    DeviceId RecipientDeviceId,
    AppId AppId,
    ReadOnlyMemory<byte> Payload,
    DeliveryRoute Route);
