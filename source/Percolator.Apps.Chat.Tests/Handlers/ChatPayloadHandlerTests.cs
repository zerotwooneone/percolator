using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Chat.Handlers;
using Percolator.Apps.Chat.Models;
using Percolator.Apps.Chat.Services;
using Percolator.Apps.Chat.Tests.TestDoubles;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Tests.Handlers;

[TestFixture]
public class ChatPayloadHandlerTests
{
    private InMemoryChatMessageRepository _chatRepo = null!;
    private InMemoryChannelRepository _channelRepo = null!;
    private InMemoryGroupInvitationRepository _invitationRepo = null!;
    private TestPayloadSerializer _serializer = null!;
    private FakeDateTimeProvider _timeProvider = null!;
    private ChatPayloadHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _chatRepo = new InMemoryChatMessageRepository();
        _channelRepo = new InMemoryChannelRepository();
        _invitationRepo = new InMemoryGroupInvitationRepository();
        _serializer = new TestPayloadSerializer();
        _timeProvider = new FakeDateTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));

        _sut = new ChatPayloadHandler(_chatRepo, _channelRepo, _invitationRepo, _serializer, _timeProvider);
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
        var record = new ChatMessageRecord(msgId, channel.Id, channel.OwnerIdentityId, _timeProvider.UtcNowUnixMs, "Original message");
        await _chatRepo.AddAsync(record);

        var reactionDto = new ReactionUpdateDto(
            msgId,
            channel.Id,
            channel.RemotePeerId,
            "❤️",
            ReactionAction.Add,
            _timeProvider.UtcNowUnixMs);

        var envelope = new ChatPayloadEnvelopeDto(ChatPayloadType.Reaction, _serializer.Serialize(reactionDto), Array.Empty<byte>());
        var context = new InboundPayloadContext(channel.Id, channel.RemotePeerId, ChatTestFactory.BobDeviceId, AppId.Chat, _serializer.Serialize(envelope), _timeProvider.UtcNow);

        var result = await _sut.HandleInboundAsync(context);

        result.IsSuccess.Should().BeTrue();
        var updated = await _chatRepo.GetByIdAsync(msgId);
        updated!.Reactions.Should().ContainSingle(r => r.Emoji == "❤️" && r.AuthorId == channel.RemotePeerId);
    }

    [Test]
    public async Task HandleInboundAsync_MessageEdit_WithinWindow_UpdatesChatRepo()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var msgId = PayloadId.New();
        var initialSentMs = _timeProvider.UtcNowUnixMs;
        var record = new ChatMessageRecord(msgId, channel.Id, channel.RemotePeerId, initialSentMs, "Initial");
        await _chatRepo.AddAsync(record);

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
    public async Task HandleInboundAsync_MessageEdit_NonAuthor_RejectsEdit()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var msgId = PayloadId.New();
        var record = new ChatMessageRecord(msgId, channel.Id, channel.OwnerIdentityId, _timeProvider.UtcNowUnixMs, "Owner text");
        await _chatRepo.AddAsync(record);

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
        var record = new ChatMessageRecord(msgId, channel.Id, channel.RemotePeerId, _timeProvider.UtcNowUnixMs, "Sensitive text");
        await _chatRepo.AddAsync(record);

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
}
