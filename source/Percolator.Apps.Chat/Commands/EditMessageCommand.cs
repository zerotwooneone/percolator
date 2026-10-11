using Percolator.Apps.Chat.Models;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Commands;

public sealed record EditMessageCommand(
    PayloadId TargetPayloadId,
    ChannelId ChannelId,
    PublicIdentityId AuthorId,
    PublicIdentityId? RecipientIdentityId,
    DeviceId RecipientDeviceId,
    DeliveryRoute Route,
    string NewContent,
    IReadOnlyList<MentionDto>? NewMentions = null);
