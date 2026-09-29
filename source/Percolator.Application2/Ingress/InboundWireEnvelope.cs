using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Ingress;

public readonly record struct InboundWireEnvelope(
    ChannelId ChannelId,
    PublicIdentityId RecipientIdentityId,
    PublicIdentityId SenderIdentityId,
    DeviceId SenderDeviceId,
    ReadOnlyMemory<byte> WirePayload,
    DateTimeOffset ReceivedAtUtc);
