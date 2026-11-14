using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.PluginSdk;

public readonly record struct InboundPayloadContext(
    ChannelId ChannelId,
    PublicIdentityId SenderIdentityId,
    DeviceId SenderDeviceId,
    AppId AppId,
    ReadOnlyMemory<byte> Payload,
    DateTimeOffset ReceivedAtUtc);
