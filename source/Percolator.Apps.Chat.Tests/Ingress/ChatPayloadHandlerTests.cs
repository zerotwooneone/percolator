using System.Collections.Concurrent;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Apps.Chat.Features.Media;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Features.Reactions;
using Percolator.Apps.Chat.Ingress;
using Percolator.Apps.Chat.Tests.TestDoubles;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Tests.Ingress;

[TestFixture]
public class ChatPayloadHandlerTests
{
    private InMemoryChatMessageRepository _chatRepo = null!;
    private InMemoryChannelRepository _channelRepo = null!;
    private InMemoryGroupInvitationRepository _invitationRepo = null!;
    private InMemoryUnknownGroupCache _unknownGroupCache = null!;
    private TestPayloadSerializer _serializer = null!;
    private FakeDateTimeProvider _timeProvider = null!;
    private ChatPayloadHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _chatRepo = new InMemoryChatMessageRepository();
        _channelRepo = new InMemoryChannelRepository();
        _invitationRepo = new InMemoryGroupInvitationRepository();
        _unknownGroupCache = new InMemoryUnknownGroupCache();
        _serializer = new TestPayloadSerializer();
        _timeProvider = new FakeDateTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));

        _sut = new ChatPayloadHandler(
            _chatRepo,
            _channelRepo,
            _invitationRepo,
            _serializer,
            _timeProvider,
            _unknownGroupCache);
    }

    [Test]
    public async Task HandleInboundAsync_TextMessage_AppendsToChannel_AndPersistsToChatMessageRepository()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var msgId = PayloadId.New();
        var msgDto = new ChatMessageDto(
            msgId,
            channel.Id,
            channel.RemotePeerId,
            _timeProvider.UtcNowUnixMs,
            "Inbound text from remote peer",
            Array.Empty<MentionDto>(),
            null,
            null,
            Array.Empty<MediaAttachmentDto>(),
            null,
            null);

        var envelope = new ChatPayloadEnvelopeDto(
            ChatPayloadType.Message,
            _serializer.Serialize(msgDto),
            new byte[128]); // with padding
        var rawPayload = _serializer.Serialize(envelope);

        var context = new InboundPayloadContext(
            channel.Id,
            channel.RemotePeerId,
            ChatTestFactory.BobDeviceId,
            AppId.Chat,
            rawPayload,
            _timeProvider.UtcNow);

        var result = await _sut.HandleInboundAsync(context);

        result.IsSuccess.Should().BeTrue(result.Error.Code + ": " + result.Error.Description);

        var record = await _chatRepo.GetByIdAsync(msgId);
        record.Should().NotBeNull();
        record!.Content.Should().Be("Inbound text from remote peer");
        record.AuthorId.Should().Be(channel.RemotePeerId);

        channel.Payloads.Should().ContainSingle(p => p.Id == msgId);
    }

    [Test]
    public async Task HandleInboundAsync_UnknownGroupMessage_BuffersInUnknownGroupCacheUntilJoined()
    {
        var unknownGroupId = ChannelId.New();
        var msgId = PayloadId.New();
        var msgDto = new ChatMessageDto(
            msgId,
            unknownGroupId,
            ChatTestFactory.BobId,
            _timeProvider.UtcNowUnixMs,
            "Early group message before invite accepted",
            Array.Empty<MentionDto>(),
            null,
            null,
            Array.Empty<MediaAttachmentDto>(),
            null,
            null);

        var envelope = new ChatPayloadEnvelopeDto(ChatPayloadType.Message, _serializer.Serialize(msgDto), Array.Empty<byte>());
        var rawPayload = _serializer.Serialize(envelope);

        var context = new InboundPayloadContext(
            unknownGroupId,
            ChatTestFactory.BobId,
            ChatTestFactory.BobDeviceId,
            AppId.Chat,
            rawPayload,
            _timeProvider.UtcNow);

        var result = await _sut.HandleInboundAsync(context);
        result.IsSuccess.Should().BeTrue();

        // Buffered in unknown group cache, not yet in active chat repository
        (await _chatRepo.GetByIdAsync(msgId)).Should().BeNull();
        (await _unknownGroupCache.GetByChannelIdAsync(unknownGroupId)).Should().ContainSingle(r => r.Id == msgId);
    }

    [Test]
    public async Task HandleInboundAsync_DiscardsSteppedPaddingCleanly()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var msgId = PayloadId.New();
        var msgDto = new ChatMessageDto(
            msgId,
            channel.Id,
            channel.RemotePeerId,
            _timeProvider.UtcNowUnixMs,
            "Text with heavy padding",
            Array.Empty<MentionDto>(),
            null,
            null,
            Array.Empty<MediaAttachmentDto>(),
            null,
            null);

        var randomPadding = new byte[1024];
        new Random(42).NextBytes(randomPadding);

        var envelope = new ChatPayloadEnvelopeDto(
            ChatPayloadType.Message,
            _serializer.Serialize(msgDto),
            randomPadding);
        var rawPayload = _serializer.Serialize(envelope);

        var context = new InboundPayloadContext(
            channel.Id,
            channel.RemotePeerId,
            ChatTestFactory.BobDeviceId,
            AppId.Chat,
            rawPayload,
            _timeProvider.UtcNow);

        var result = await _sut.HandleInboundAsync(context);

        result.IsSuccess.Should().BeTrue();
        var record = await _chatRepo.GetByIdAsync(msgId);
        record!.Content.Should().Be("Text with heavy padding");
    }

    [Test]
    public async Task HandleInboundAsync_WithMentions_ValidatesUtf8ByteBoundaries()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var text = "Hello @alice";
        var mention = MentionHelper.Create(text, 6, 6, channel.OwnerIdentityId);

        var msgId = PayloadId.New();
        var msgDto = new ChatMessageDto(
            msgId,
            channel.Id,
            channel.RemotePeerId,
            _timeProvider.UtcNowUnixMs,
            text,
            new[] { mention },
            null,
            null,
            Array.Empty<MediaAttachmentDto>(),
            null,
            null);

        var envelope = new ChatPayloadEnvelopeDto(ChatPayloadType.Message, _serializer.Serialize(msgDto), Array.Empty<byte>());
        var rawPayload = _serializer.Serialize(envelope);

        var context = new InboundPayloadContext(channel.Id, channel.RemotePeerId, ChatTestFactory.BobDeviceId, AppId.Chat, rawPayload, _timeProvider.UtcNow);
        var result = await _sut.HandleInboundAsync(context);

        result.IsSuccess.Should().BeTrue();
        var record = await _chatRepo.GetByIdAsync(msgId);
        record!.Mentions.Should().ContainSingle();
        record.Mentions[0].StartOffset.Should().Be(6);
    }

    [Test]
    public async Task HandleInboundAsync_EmojiReaction_AppliesReactionToChatRepo()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var msgId = PayloadId.New();
        var message = new ChatMessage(msgId, channel.Id, channel.OwnerIdentityId, _timeProvider.UtcNowUnixMs, "Original message");
        await _chatRepo.AddAsync(message);

        var reactionDto = new ReactionUpdateDto(
            msgId,
            channel.Id,
            channel.RemotePeerId,
            "\u2764\ufe0f",
            ReactionAction.Add,
            _timeProvider.UtcNowUnixMs);

        var envelope = new ChatPayloadEnvelopeDto(ChatPayloadType.Reaction, _serializer.Serialize(reactionDto), Array.Empty<byte>());
        var context = new InboundPayloadContext(channel.Id, channel.RemotePeerId, ChatTestFactory.BobDeviceId, AppId.Chat, _serializer.Serialize(envelope), _timeProvider.UtcNow);

        var result = await _sut.HandleInboundAsync(context);

        result.IsSuccess.Should().BeTrue();
        var updated = await _chatRepo.GetByIdAsync(msgId);
        updated!.Reactions.Should().ContainSingle(r => r.Emoji.Value == "\u2764\ufe0f" && r.ReactorId == channel.RemotePeerId);
    }

    [Test]
    public async Task HandleInboundAsync_InvalidEmoji_ReturnsInvalidEmoji()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var msgId = PayloadId.New();
        var message = new ChatMessage(msgId, channel.Id, channel.OwnerIdentityId, _timeProvider.UtcNowUnixMs, "Original message");
        await _chatRepo.AddAsync(message);

        var reactionDto = new ReactionUpdateDto(
            msgId,
            channel.Id,
            channel.RemotePeerId,
            "NOT_AN_EMOJI_AND_TOO_LONG",
            ReactionAction.Add,
            _timeProvider.UtcNowUnixMs);

        var envelope = new ChatPayloadEnvelopeDto(ChatPayloadType.Reaction, _serializer.Serialize(reactionDto), Array.Empty<byte>());
        var context = new InboundPayloadContext(channel.Id, channel.RemotePeerId, ChatTestFactory.BobDeviceId, AppId.Chat, _serializer.Serialize(envelope), _timeProvider.UtcNow);

        var result = await _sut.HandleInboundAsync(context);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_EMOJI");
    }

    [Test]
    public async Task HandleInboundAsync_MessageEdit_WithinWindow_UpdatesChatRepo()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var msgId = PayloadId.New();
        var initialSentMs = _timeProvider.UtcNowUnixMs;
        var message = new ChatMessage(msgId, channel.Id, channel.RemotePeerId, initialSentMs, "Initial");
        await _chatRepo.AddAsync(message);

        _timeProvider.Advance(TimeSpan.FromHours(1));

        var editDto = new MessageEditDto(
            msgId,
            channel.Id,
            channel.RemotePeerId,
            "Edited remote text",
            Array.Empty<MentionDto>(),
            _timeProvider.UtcNowUnixMs);

        var envelope = new ChatPayloadEnvelopeDto(ChatPayloadType.Edit, _serializer.Serialize(editDto), Array.Empty<byte>());
        var context = new InboundPayloadContext(channel.Id, channel.RemotePeerId, ChatTestFactory.BobDeviceId, AppId.Chat, _serializer.Serialize(envelope), _timeProvider.UtcNow);

        var result = await _sut.HandleInboundAsync(context);

        result.IsSuccess.Should().BeTrue();
        var updated = await _chatRepo.GetByIdAsync(msgId);
        updated!.Content.Should().Be("Edited remote text");
        updated.EditedAtUnixMs.Should().Be(_timeProvider.UtcNowUnixMs);
    }

    [Test]
    public async Task HandleInboundAsync_MessageEdit_WhenSenderSpoofsTimestampPast24HourReceiverWindow_RejectsEdit()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var msgId = PayloadId.New();
        var initialSentMs = _timeProvider.UtcNowUnixMs;
        var message = new ChatMessage(msgId, channel.Id, channel.RemotePeerId, initialSentMs, "Initial");
        await _chatRepo.AddAsync(message);

        // Receiver clock is now 30 hours later, but sender spoofs EditedAtUnixMs = initialSentMs + 1000
        _timeProvider.Advance(TimeSpan.FromHours(30));

        var spoofedEditDto = new MessageEditDto(
            msgId,
            channel.Id,
            channel.RemotePeerId,
            "Spoofed backdated edit",
            Array.Empty<MentionDto>(),
            initialSentMs + 1000);

        var envelope = new ChatPayloadEnvelopeDto(ChatPayloadType.Edit, _serializer.Serialize(spoofedEditDto), Array.Empty<byte>());
        var context = new InboundPayloadContext(channel.Id, channel.RemotePeerId, ChatTestFactory.BobDeviceId, AppId.Chat, _serializer.Serialize(envelope), _timeProvider.UtcNow);

        var result = await _sut.HandleInboundAsync(context);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EDIT_WINDOW_EXPIRED");
    }

    [Test]
    public async Task HandleInboundAsync_MessageEdit_NonAuthor_RejectsEdit()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var msgId = PayloadId.New();
        var message = new ChatMessage(msgId, channel.Id, channel.OwnerIdentityId, _timeProvider.UtcNowUnixMs, "Owner text");
        await _chatRepo.AddAsync(message);

        var editDto = new MessageEditDto(
            msgId,
            channel.Id,
            channel.RemotePeerId, // Spoofed or non-author
            "Attempted hijack",
            Array.Empty<MentionDto>(),
            _timeProvider.UtcNowUnixMs);

        var envelope = new ChatPayloadEnvelopeDto(ChatPayloadType.Edit, _serializer.Serialize(editDto), Array.Empty<byte>());
        var context = new InboundPayloadContext(channel.Id, channel.RemotePeerId, ChatTestFactory.BobDeviceId, AppId.Chat, _serializer.Serialize(envelope), _timeProvider.UtcNow);

        var result = await _sut.HandleInboundAsync(context);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UNAUTHORIZED_AUTHOR");
    }

    [Test]
    public async Task HandleInboundAsync_MessageTombstone_ReplacesWithDeletedPlaceholder()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var msgId = PayloadId.New();
        var message = new ChatMessage(msgId, channel.Id, channel.RemotePeerId, _timeProvider.UtcNowUnixMs, "Sensitive text");
        await _chatRepo.AddAsync(message);

        var tombstoneDto = new MessageTombstoneDto(
            msgId,
            channel.Id,
            channel.RemotePeerId,
            _timeProvider.UtcNowUnixMs);

        var envelope = new ChatPayloadEnvelopeDto(ChatPayloadType.Tombstone, _serializer.Serialize(tombstoneDto), Array.Empty<byte>());
        var context = new InboundPayloadContext(channel.Id, channel.RemotePeerId, ChatTestFactory.BobDeviceId, AppId.Chat, _serializer.Serialize(envelope), _timeProvider.UtcNow);

        var result = await _sut.HandleInboundAsync(context);

        result.IsSuccess.Should().BeTrue();
        var updated = await _chatRepo.GetByIdAsync(msgId);
        updated!.IsTombstone.Should().BeTrue();
        updated.Content.Should().Be("This message was deleted");
    }

    [Test]
    public async Task HandleInboundAsync_ReadReceipt_StartsDisappearingTimerAndRejectsForgedReader()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var msgId = PayloadId.New();
        var message = new ChatMessage(
            msgId,
            channel.Id,
            channel.OwnerIdentityId,
            _timeProvider.UtcNowUnixMs,
            "Ephemeral 1:1 message",
            expireAfterSeconds: 30,
            expiresAtUnixMs: null);
        await _chatRepo.AddAsync(message);

        _timeProvider.Advance(TimeSpan.FromMinutes(2));

        // 1. Forged reader identity is rejected
        var forgedReceipt = new ReadReceiptDto(channel.Id, channel.OwnerIdentityId, msgId, _timeProvider.UtcNowUnixMs);
        var forgedEnv = new ChatPayloadEnvelopeDto(ChatPayloadType.ReadReceipt, _serializer.Serialize(forgedReceipt), Array.Empty<byte>());
        var forgedCtx = new InboundPayloadContext(channel.Id, channel.RemotePeerId, ChatTestFactory.BobDeviceId, AppId.Chat, _serializer.Serialize(forgedEnv), _timeProvider.UtcNow);
        var forgedResult = await _sut.HandleInboundAsync(forgedCtx);
        forgedResult.IsFailure.Should().BeTrue();
        forgedResult.Error.Code.Should().Be("UNAUTHORIZED_AUTHOR");

        // 2. Valid read receipt marks message read and starts 30s expiration countdown
        var validReceipt = new ReadReceiptDto(channel.Id, channel.RemotePeerId, msgId, _timeProvider.UtcNowUnixMs);
        var validEnv = new ChatPayloadEnvelopeDto(ChatPayloadType.ReadReceipt, _serializer.Serialize(validReceipt), Array.Empty<byte>());
        var validCtx = new InboundPayloadContext(channel.Id, channel.RemotePeerId, ChatTestFactory.BobDeviceId, AppId.Chat, _serializer.Serialize(validEnv), _timeProvider.UtcNow);
        var validResult = await _sut.HandleInboundAsync(validCtx);
        validResult.IsSuccess.Should().BeTrue();

        var updated = await _chatRepo.GetByIdAsync(msgId);
        updated!.ReadAtUnixMs.Should().Be(_timeProvider.UtcNowUnixMs);
        updated.ExpiresAtUnixMs.Should().Be(_timeProvider.UtcNowUnixMs + 30_000UL);
    }

    [Test]
    public void ChatIngressFilter_RejectsOversizedFramesAndRateLimitsFloods()
    {
        var filter = new ChatIngressFilter(_timeProvider, maxFramesPerWindow: 2);
        var channelId = ChannelId.New();

        // 1. Oversized frame (> 64 KB) is dropped before deserialization
        var oversized = new InboundPayloadContext(
            channelId,
            ChatTestFactory.BobId,
            ChatTestFactory.BobDeviceId,
            AppId.Chat,
            new byte[ChatIngressFilter.MaxWireFrameBytes + 1],
            _timeProvider.UtcNow);
        filter.Evaluate(oversized).Should().Be(IngressDecision.DiscardSilently);

        // 2. Valid frames accepted up to rate limit
        var validFrame = new InboundPayloadContext(
            channelId,
            ChatTestFactory.BobId,
            ChatTestFactory.BobDeviceId,
            AppId.Chat,
            new byte[256],
            _timeProvider.UtcNow);
        filter.Evaluate(validFrame).Should().Be(IngressDecision.Accept);
        filter.Evaluate(validFrame).Should().Be(IngressDecision.Accept);
        filter.Evaluate(validFrame).Should().Be(IngressDecision.DiscardSilently);
    }

    [Test]
    public async Task HandleInboundAsync_CorruptedEnvelope_ReturnsDeserializationError()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        var garbage = new byte[] { 0xFF, 0xFE, 0x00, 0x01 };

        var context = new InboundPayloadContext(channel.Id, channel.RemotePeerId, ChatTestFactory.BobDeviceId, AppId.Chat, garbage, _timeProvider.UtcNow);
        var result = await _sut.HandleInboundAsync(context);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("DESERIALIZATION_FAILED");
    }

    [Test]
    public async Task HandleInboundAsync_FutureUnknownType_ReturnsSuccessWithoutError()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        var unknownType = (ChatPayloadType)99;

        var envelope = new ChatPayloadEnvelopeDto(unknownType, new byte[] { 1, 2, 3 }, Array.Empty<byte>());
        var context = new InboundPayloadContext(channel.Id, channel.RemotePeerId, ChatTestFactory.BobDeviceId, AppId.Chat, _serializer.Serialize(envelope), _timeProvider.UtcNow);

        var result = await _sut.HandleInboundAsync(context);

        result.IsSuccess.Should().BeTrue();
    }

    #region Security Tests (Step 2.3)

    [Test]
    public async Task HandleInboundAsync_ForgedReactionAuthor_ReturnsUnauthorizedAuthor()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var msgId = PayloadId.New();
        var message = new ChatMessage(msgId, channel.Id, channel.OwnerIdentityId, _timeProvider.UtcNowUnixMs, "Original message");
        await _chatRepo.AddAsync(message);

        // Sender in context is Bob, but DTO claims reaction author is Alice
        var forgedReactionDto = new ReactionUpdateDto(
            msgId,
            channel.Id,
            channel.OwnerIdentityId, // Forged author!
            "\ud83d\udc4d",
            ReactionAction.Add,
            _timeProvider.UtcNowUnixMs);

        var envelope = new ChatPayloadEnvelopeDto(ChatPayloadType.Reaction, _serializer.Serialize(forgedReactionDto), Array.Empty<byte>());
        var context = new InboundPayloadContext(channel.Id, channel.RemotePeerId, ChatTestFactory.BobDeviceId, AppId.Chat, _serializer.Serialize(envelope), _timeProvider.UtcNow);

        var result = await _sut.HandleInboundAsync(context);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UNAUTHORIZED_AUTHOR");
    }

    [Test]
    public async Task HandleInboundAsync_CrossChannelPayload_ReturnsChannelMismatch()
    {
        var channelA = ChatTestFactory.CreateDirectChannel(_timeProvider);
        var channelB = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channelA);
        await _channelRepo.SaveDirectAsync(channelB);

        var msgId = PayloadId.New();
        // Message is in channelA
        var message = new ChatMessage(msgId, channelA.Id, channelA.RemotePeerId, _timeProvider.UtcNowUnixMs, "Original message");
        await _chatRepo.AddAsync(message);

        // Reaction specifies channelB, delivered over channelB context
        var crossChannelReaction = new ReactionUpdateDto(
            msgId,
            channelB.Id,
            channelA.RemotePeerId,
            "\ud83d\udc4d",
            ReactionAction.Add,
            _timeProvider.UtcNowUnixMs);

        var envelope = new ChatPayloadEnvelopeDto(ChatPayloadType.Reaction, _serializer.Serialize(crossChannelReaction), Array.Empty<byte>());
        var context = new InboundPayloadContext(channelB.Id, channelA.RemotePeerId, ChatTestFactory.BobDeviceId, AppId.Chat, _serializer.Serialize(envelope), _timeProvider.UtcNow);

        var result = await _sut.HandleInboundAsync(context);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("CHANNEL_MISMATCH");
    }

    [Test]
    public async Task HandleInboundAsync_ReplayedMessage_DoesNotOverwriteTombstone()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var msgId = PayloadId.New();
        var message = new ChatMessage(msgId, channel.Id, channel.RemotePeerId, _timeProvider.UtcNowUnixMs, "Secret message");
        message.ApplyTombstone(channel.RemotePeerId, _timeProvider.UtcNowUnixMs + 500);
        await _chatRepo.AddAsync(message);

        // Attacker replays initial message DTO
        var replayDto = new ChatMessageDto(
            msgId,
            channel.Id,
            channel.RemotePeerId,
            _timeProvider.UtcNowUnixMs,
            "Secret message resurrected",
            Array.Empty<MentionDto>(),
            null,
            null,
            Array.Empty<MediaAttachmentDto>(),
            null,
            null);

        var envelope = new ChatPayloadEnvelopeDto(ChatPayloadType.Message, _serializer.Serialize(replayDto), Array.Empty<byte>());
        var context = new InboundPayloadContext(channel.Id, channel.RemotePeerId, ChatTestFactory.BobDeviceId, AppId.Chat, _serializer.Serialize(envelope), _timeProvider.UtcNow);

        var result = await _sut.HandleInboundAsync(context);

        result.IsSuccess.Should().BeTrue(); // Idempotently accepted
        var stored = await _chatRepo.GetByIdAsync(msgId);
        stored!.IsTombstone.Should().BeTrue();
        stored.Content.Should().Be("This message was deleted"); // Not overwritten!
    }

    [Test]
    public async Task HandleInboundAsync_ForgedInvitationSender_ReturnsUnauthorized()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        // Sender context is Bob, but invite claims inviter is Alice
        var forgedInvite = new GroupInvitationDto(
            new InvitationId("invite-999"),
            ChannelId.New(),
            "Hacker Group",
            channel.OwnerIdentityId, // Forged inviter!
            null,
            _timeProvider.UtcNowUnixMs + 3600_000);

        var envelope = new ChatPayloadEnvelopeDto(ChatPayloadType.GroupInvitation, _serializer.Serialize(forgedInvite), Array.Empty<byte>());
        var context = new InboundPayloadContext(channel.Id, channel.RemotePeerId, ChatTestFactory.BobDeviceId, AppId.Chat, _serializer.Serialize(envelope), _timeProvider.UtcNow);

        var result = await _sut.HandleInboundAsync(context);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UNAUTHORIZED_AUTHOR");
    }

    [Test]
    public async Task HandleInboundAsync_GroupAdminTombstone_WhenAdmin_SucceedsWithAdminPlaceholder()
    {
        var group = ChatTestFactory.CreateGroupChannel(_timeProvider);
        var adminId = group.Members.First().Id;
        var authorId = PublicIdentityId.New();
        group.AddMember(adminId, authorId, ChannelRole.Member, _timeProvider);
        await _channelRepo.SaveGroupAsync(group);

        var msgId = PayloadId.New();
        var message = new ChatMessage(msgId, group.Id, authorId, _timeProvider.UtcNowUnixMs, "Abusive text");
        await _chatRepo.AddAsync(message);

        // Admin (group creator) sends tombstone
        var tombstoneDto = new MessageTombstoneDto(
            msgId,
            group.Id,
            adminId,
            _timeProvider.UtcNowUnixMs + 100);

        var envelope = new ChatPayloadEnvelopeDto(ChatPayloadType.Tombstone, _serializer.Serialize(tombstoneDto), Array.Empty<byte>());
        var context = new InboundPayloadContext(group.Id, adminId, ChatTestFactory.AliceDeviceId, AppId.Chat, _serializer.Serialize(envelope), _timeProvider.UtcNow);

        var result = await _sut.HandleInboundAsync(context);

        result.IsSuccess.Should().BeTrue();
        var updated = await _chatRepo.GetByIdAsync(msgId);
        updated!.IsTombstone.Should().BeTrue();
        updated.Content.Should().Be("This message was deleted by a channel administrator");
        updated.DeletedBy.Should().Be(adminId);
    }

    [Test]
    public async Task HandleInboundAsync_GroupAdminTombstone_WhenNonAdmin_ReturnsUnauthorizedRole()
    {
        var group = ChatTestFactory.CreateGroupChannel(_timeProvider);
        var adminId = group.Members.First().Id;
        var authorId = PublicIdentityId.New();
        var nonAdminId = PublicIdentityId.New();
        group.AddMember(adminId, authorId, ChannelRole.Member, _timeProvider);
        group.AddMember(adminId, nonAdminId, ChannelRole.Member, _timeProvider);
        await _channelRepo.SaveGroupAsync(group);

        var msgId = PayloadId.New();
        var message = new ChatMessage(msgId, group.Id, authorId, _timeProvider.UtcNowUnixMs, "Normal text");
        await _chatRepo.AddAsync(message);

        // Non-admin member tries to tombstone other user's message
        var tombstoneDto = new MessageTombstoneDto(
            msgId,
            group.Id,
            nonAdminId,
            _timeProvider.UtcNowUnixMs + 100);

        var envelope = new ChatPayloadEnvelopeDto(ChatPayloadType.Tombstone, _serializer.Serialize(tombstoneDto), Array.Empty<byte>());
        var context = new InboundPayloadContext(group.Id, nonAdminId, ChatTestFactory.BobDeviceId, AppId.Chat, _serializer.Serialize(envelope), _timeProvider.UtcNow);

        var result = await _sut.HandleInboundAsync(context);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UNAUTHORIZED_ROLE");
    }

    [Test]
    public async Task HandleInboundAsync_GroupMessage_AppendsToGroupChannelAggregate()
    {
        var group = ChatTestFactory.CreateGroupChannel(_timeProvider);
        var adminId = group.Members.First().Id;
        var senderId = PublicIdentityId.New();
        group.AddMember(adminId, senderId, ChannelRole.Member, _timeProvider);
        await _channelRepo.SaveGroupAsync(group);

        var msgId = PayloadId.New();
        var msgDto = new ChatMessageDto(
            msgId,
            group.Id,
            senderId,
            _timeProvider.UtcNowUnixMs,
            "Group hello!",
            Array.Empty<MentionDto>(),
            null,
            null,
            Array.Empty<MediaAttachmentDto>(),
            null,
            null);

        var envelope = new ChatPayloadEnvelopeDto(ChatPayloadType.Message, _serializer.Serialize(msgDto), Array.Empty<byte>());
        var context = new InboundPayloadContext(group.Id, senderId, ChatTestFactory.BobDeviceId, AppId.Chat, _serializer.Serialize(envelope), _timeProvider.UtcNow);

        var result = await _sut.HandleInboundAsync(context);

        result.IsSuccess.Should().BeTrue();
        group.Payloads.Should().ContainSingle(p => p.Id == msgId);
    }

    #endregion

    private sealed class InMemoryUnknownGroupCache : IUnknownGroupMessageCacheRepository
    {
        private readonly ConcurrentDictionary<PayloadId, UnknownGroupMessageRecord> _records = new();

        public Task AddAsync(UnknownGroupMessageRecord record, CancellationToken ct = default)
        {
            _records[record.Id] = record;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<UnknownGroupMessageRecord>> GetByChannelIdAsync(ChannelId channelId, CancellationToken ct = default)
        {
            var list = _records.Values.Where(r => r.ChannelId == channelId).ToList();
            return Task.FromResult<IReadOnlyList<UnknownGroupMessageRecord>>(list);
        }

        public Task RemoveAsync(PayloadId id, CancellationToken ct = default)
        {
            _records.TryRemove(id, out _);
            return Task.CompletedTask;
        }

        public Task<int> PurgeExpiredAsync(ulong currentTimestampUnixMs, CancellationToken ct = default)
        {
            int count = 0;
            foreach (var kvp in _records)
            {
                if (kvp.Value.ExpiresAtUnixMs <= currentTimestampUnixMs && _records.TryRemove(kvp.Key, out _))
                {
                    count++;
                }
            }
            return Task.FromResult(count);
        }
    }
}
