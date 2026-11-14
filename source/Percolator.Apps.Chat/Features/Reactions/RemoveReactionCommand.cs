using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.Chat.Features.Reactions;

public sealed record RemoveReactionCommand(
    PayloadId TargetPayloadId,
    ChannelId ChannelId,
    PublicIdentityId AuthorId,
    PublicIdentityId? RecipientIdentityId,
    DeviceId RecipientDeviceId,
    DeliveryRoute Route,
    Emoji Emoji);
