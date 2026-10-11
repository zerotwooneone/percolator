using Percolator.Apps.Chat.Features.Media;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Features.Messaging;

public sealed record SendMessageCommand(
    ChannelId ChannelId,
    PublicIdentityId AuthorId,
    PublicIdentityId? RecipientIdentityId,
    DeviceId RecipientDeviceId,
    DeliveryRoute Route,
    string Content,
    IReadOnlyList<MentionDto>? Mentions = null,
    ReplyContextDto? ReplyContext = null,
    LinkPreviewDto? LinkPreview = null,
    IReadOnlyList<MediaAttachmentDto>? Attachments = null,
    int? ExpireAfterSeconds = null);
