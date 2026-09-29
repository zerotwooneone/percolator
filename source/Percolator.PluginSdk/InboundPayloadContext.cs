using Percolator.Domain.Conversations.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.PluginSdk;

public readonly record struct InboundPayloadContext(
    ConversationId ConversationId,
    PublicIdentityId SenderIdentityId,
    DeviceId SenderDeviceId,
    AppId AppId,
    ReadOnlyMemory<byte> Payload,
    DateTimeOffset ReceivedAtUtc);
