using Percolator.Apps.Chat.Models;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Commands;

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
