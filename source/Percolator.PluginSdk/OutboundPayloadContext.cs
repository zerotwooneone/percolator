using Percolator.Domain.Conversations.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.PluginSdk;

public sealed record OutboundPayloadContext(
    ConversationId ConversationId,
    PublicIdentityId RecipientIdentityId,
    AppId AppId,
    ReadOnlyMemory<byte> Payload,
    DeliveryRoute Route);
