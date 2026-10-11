using Percolator.Apps.Chat.Features.Media;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Tests.TestDoubles;

public static class ChatTestFactory
{
    public static readonly PublicIdentityId AliceId = PublicIdentityId.New();
    public static readonly DeviceId AliceDeviceId = DeviceId.Primary;

    public static readonly PublicIdentityId BobId = PublicIdentityId.New();
    public static readonly DeviceId BobDeviceId = DeviceId.Primary;

    public static DirectChannel CreateDirectChannel(
        IDateTimeProvider timeProvider,
        ChannelId? channelId = null,
        PublicIdentityId? ownerId = null,
        PublicIdentityId? peerId = null)
    {
        var id = channelId ?? ChannelId.New();
        var owner = ownerId ?? AliceId;
        var peer = peerId ?? BobId;
        return DirectChannel.Create(id, owner, peer, timeProvider).Value;
    }

    public static GroupChannel CreateGroupChannel(
        IDateTimeProvider timeProvider,
        ChannelId? channelId = null,
        PublicIdentityId? creatorId = null,
        string name = "Test Group")
    {
        var id = channelId ?? ChannelId.New();
        var creator = creatorId ?? AliceId;
        return GroupChannel.CreateGenesis(id, creator, name, timeProvider).Value;
    }

    public static SendMessageCommand CreateSendMessageCommand(
        ChannelId? channelId = null,
        PublicIdentityId? authorId = null,
        PublicIdentityId? recipientId = null,
        string content = "Hello, world!",
        IReadOnlyList<MentionDto>? mentions = null,
        ReplyContextDto? replyContext = null,
        LinkPreviewDto? linkPreview = null,
        IReadOnlyList<MediaAttachmentDto>? attachments = null,
        int? expireAfterSeconds = null)
    {
        return new SendMessageCommand(
            channelId ?? ChannelId.New(),
            authorId ?? AliceId,
            recipientId ?? BobId,
            BobDeviceId,
            DeliveryRoute.DirectP2P,
            content,
            mentions,
            replyContext,
            linkPreview,
            attachments,
            expireAfterSeconds);
    }
}
