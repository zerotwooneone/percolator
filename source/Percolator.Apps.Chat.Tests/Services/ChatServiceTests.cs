using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Chat.Commands;
using Percolator.Apps.Chat.Models;
using Percolator.Apps.Chat.Services;
using Percolator.Apps.Chat.Tests.TestDoubles;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Tests.Services;

[TestFixture]
public class ChatServiceTests
{
    private InMemoryChatMessageRepository _chatRepo = null!;
    private InMemoryChannelRepository _channelRepo = null!;
    private TestPayloadSender _payloadSender = null!;
    private TestPayloadSerializer _serializer = null!;
    private FakeDateTimeProvider _timeProvider = null!;
    private ChatService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _chatRepo = new InMemoryChatMessageRepository();
        _channelRepo = new InMemoryChannelRepository();
        _payloadSender = new TestPayloadSender();
        _serializer = new TestPayloadSerializer();
        _timeProvider = new FakeDateTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, 123, TimeSpan.Zero)); // fractional ms

        _sut = new ChatService(_chatRepo, _channelRepo, _payloadSender, _serializer, _timeProvider);
    }

    [Test]
    public async Task SendMessageAsync_ValidText_AppendsChannelPayload_AndDispatchesPaddedEnvelope()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var cmd = ChatTestFactory.CreateSendMessageCommand(
            channelId: channel.Id,
            authorId: channel.OwnerIdentityId,
            recipientId: channel.RemotePeerId,
            content: "Hello world!");

        var result = await _sut.SendMessageAsync(cmd);

        result.IsSuccess.Should().BeTrue();
        var messageId = result.Value;

        // Persisted to chat repository
        var savedRecord = await _chatRepo.GetByIdAsync(messageId);
        savedRecord.Should().NotBeNull();
        savedRecord!.Content.Should().Be("Hello world!");

        // Appended to domain channel aggregate
        channel.Payloads.Should().ContainSingle(p => p.Id == messageId);

        // Dispatched via IPayloadSender with padding
        _payloadSender.SentPayloads.Should().ContainSingle();
        var sent = _payloadSender.SentPayloads[0];
        sent.ChannelId.Should().Be(channel.Id);
        sent.AppId.Should().Be(AppId.Chat);

        var envelopeResult = _serializer.Deserialize<ChatPayloadEnvelopeDto>(sent.Payload);
        envelopeResult.IsSuccess.Should().BeTrue();
        var envelope = envelopeResult.Value;
        envelope.Type.Should().Be(ChatPayloadType.Message);
        envelope.Padding.Length.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task SendMessageAsync_ClampsTimestampToUnixMilliseconds()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var cmd = ChatTestFactory.CreateSendMessageCommand(channelId: channel.Id, authorId: channel.OwnerIdentityId, recipientId: channel.RemotePeerId);

        var result = await _sut.SendMessageAsync(cmd);
        result.IsSuccess.Should().BeTrue();

        var saved = await _chatRepo.GetByIdAsync(result.Value);
        saved!.TimestampUnixMs.Should().Be(_timeProvider.UtcNowUnixMs);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task SendMessageAsync_WithInvalidOrEmptyContent_ReturnsValidationError(string? invalidContent)
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var cmd = ChatTestFactory.CreateSendMessageCommand(channelId: channel.Id, content: invalidContent!);

        var result = await _sut.SendMessageAsync(cmd);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_CONTENT");
    }

    [Test]
    public async Task EditMessageAsync_Within24HourWindow_UpdatesRepository_AndDispatchesEditDto()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var sendCmd = ChatTestFactory.CreateSendMessageCommand(channelId: channel.Id, authorId: channel.OwnerIdentityId, recipientId: channel.RemotePeerId, content: "Original");
        var sendResult = await _sut.SendMessageAsync(sendCmd);
        var messageId = sendResult.Value;

        // Advance 2 hours
        _timeProvider.Advance(TimeSpan.FromHours(2));

        var editCmd = new EditMessageCommand(
            messageId,
            channel.Id,
            channel.OwnerIdentityId,
            channel.RemotePeerId,
            ChatTestFactory.BobDeviceId,
            DeliveryRoute.DirectP2P,
            "Updated content");

        var editResult = await _sut.EditMessageAsync(editCmd);

        editResult.IsSuccess.Should().BeTrue();
        var updated = await _chatRepo.GetByIdAsync(messageId);
        updated!.Content.Should().Be("Updated content");
        updated.EditedAtUnixMs.Should().Be(_timeProvider.UtcNowUnixMs);

        // Edit dispatched
        var lastSent = _payloadSender.SentPayloads.Last();
        var envelope = _serializer.Deserialize<ChatPayloadEnvelopeDto>(lastSent.Payload).Value;
        envelope.Type.Should().Be(ChatPayloadType.Edit);
    }

    [Test]
    public async Task EditMessageAsync_Past24HourWindow_ReturnsEditWindowExpiredError()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var sendCmd = ChatTestFactory.CreateSendMessageCommand(channelId: channel.Id, authorId: channel.OwnerIdentityId, recipientId: channel.RemotePeerId, content: "Original");
        var sendResult = await _sut.SendMessageAsync(sendCmd);
        var messageId = sendResult.Value;

        // Advance 25 hours
        _timeProvider.Advance(TimeSpan.FromHours(25));

        var editCmd = new EditMessageCommand(
            messageId,
            channel.Id,
            channel.OwnerIdentityId,
            channel.RemotePeerId,
            ChatTestFactory.BobDeviceId,
            DeliveryRoute.DirectP2P,
            "Late edit");

        var editResult = await _sut.EditMessageAsync(editCmd);

        editResult.IsFailure.Should().BeTrue();
        editResult.Error.Code.Should().Be("EDIT_WINDOW_EXPIRED");
    }

    [Test]
    public async Task EditMessageAsync_ByNonAuthor_ReturnsUnauthorizedError()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var sendCmd = ChatTestFactory.CreateSendMessageCommand(channelId: channel.Id, authorId: channel.OwnerIdentityId, recipientId: channel.RemotePeerId, content: "Original");
        var sendResult = await _sut.SendMessageAsync(sendCmd);
        var messageId = sendResult.Value;

        var editCmd = new EditMessageCommand(
            messageId,
            channel.Id,
            channel.RemotePeerId, // Non-author
            channel.OwnerIdentityId,
            ChatTestFactory.AliceDeviceId,
            DeliveryRoute.DirectP2P,
            "Spoofed edit");

        var editResult = await _sut.EditMessageAsync(editCmd);

        editResult.IsFailure.Should().BeTrue();
        editResult.Error.Code.Should().Be("UNAUTHORIZED_AUTHOR");
    }

    [Test]
    public async Task DeleteMessageAsync_Author_MarksTombstone_AndDispatchesTombstoneDto()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var sendCmd = ChatTestFactory.CreateSendMessageCommand(channelId: channel.Id, authorId: channel.OwnerIdentityId, recipientId: channel.RemotePeerId, content: "To be deleted");
        var sendResult = await _sut.SendMessageAsync(sendCmd);
        var messageId = sendResult.Value;

        var deleteCmd = new DeleteMessageCommand(
            messageId,
            channel.Id,
            channel.OwnerIdentityId,
            channel.RemotePeerId,
            ChatTestFactory.BobDeviceId,
            DeliveryRoute.DirectP2P);

        var deleteResult = await _sut.DeleteMessageAsync(deleteCmd);

        deleteResult.IsSuccess.Should().BeTrue();
        var record = await _chatRepo.GetByIdAsync(messageId);
        record!.IsTombstone.Should().BeTrue();
        record.Content.Should().Be("This message was deleted");

        var lastSent = _payloadSender.SentPayloads.Last();
        var envelope = _serializer.Deserialize<ChatPayloadEnvelopeDto>(lastSent.Payload).Value;
        envelope.Type.Should().Be(ChatPayloadType.Tombstone);
    }

    [Test]
    public async Task AddReactionAsync_RepeatedCalls_AreIdempotent()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var sendCmd = ChatTestFactory.CreateSendMessageCommand(channelId: channel.Id, authorId: channel.OwnerIdentityId, recipientId: channel.RemotePeerId, content: "Nice work");
        var sendResult = await _sut.SendMessageAsync(sendCmd);
        var messageId = sendResult.Value;

        var reactCmd = new AddReactionCommand(
            messageId,
            channel.Id,
            channel.OwnerIdentityId,
            channel.RemotePeerId,
            ChatTestFactory.BobDeviceId,
            DeliveryRoute.DirectP2P,
            "🚀");

        var first = await _sut.AddReactionAsync(reactCmd);
        var second = await _sut.AddReactionAsync(reactCmd);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();

        var record = await _chatRepo.GetByIdAsync(messageId);
        record!.Reactions.Should().HaveCount(1);
    }

    [Test]
    public async Task MarkAsReadAsync_AdvancesCursorAndDispatchesReadReceiptDto()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var readCmd = new MarkAsReadCommand(
            channel.Id,
            channel.RemotePeerId,
            PayloadId.New(),
            channel.OwnerIdentityId,
            ChatTestFactory.AliceDeviceId,
            DeliveryRoute.DirectP2P);

        var result = await _sut.MarkAsReadAsync(readCmd);

        result.IsSuccess.Should().BeTrue();
        var lastSent = _payloadSender.SentPayloads.Last();
        var envelope = _serializer.Deserialize<ChatPayloadEnvelopeDto>(lastSent.Payload).Value;
        envelope.Type.Should().Be(ChatPayloadType.ReadReceipt);
    }
}
