using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.Chat.Features.Messaging;

public sealed record EditMessageCommand(
    PayloadId TargetPayloadId,
    ChannelId ChannelId,
    PublicIdentityId AuthorId,
    PublicIdentityId? RecipientIdentityId,
    DeviceId RecipientDeviceId,
    DeliveryRoute Route,
    string NewContent,
    IReadOnlyList<MentionDto>? NewMentions = null);
