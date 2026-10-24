using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Chat.Features.Media;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Features.Profiles;
using Percolator.Apps.Chat.Features.Reactions;
using Percolator.Apps.Chat.Tests.TestDoubles;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Tests.Features.Messaging;

[TestFixture]
public class ChatServiceTests
{
    private InMemoryChatMessageRepository _chatRepo = null!;
    private InMemoryChannelRepository _channelRepo = null!;
    private TestPayloadSender _payloadSender = null!;
    private TestPayloadSerializer _serializer = null!;
    private FakeDateTimeProvider _timeProvider = null!;
    private FakeBlobStorageService _blobStorage = null!;
    private ChatService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _chatRepo = new InMemoryChatMessageRepository();
        _channelRepo = new InMemoryChannelRepository();
        _payloadSender = new TestPayloadSender();
        _serializer = new TestPayloadSerializer();
        _timeProvider = new FakeDateTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, 123, TimeSpan.Zero)); // fractional ms
        _blobStorage = new FakeBlobStorageService();

        _sut = new ChatService(_chatRepo, _channelRepo, _payloadSender, _serializer, _timeProvider, _blobStorage);
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
    public async Task SendMessageAsync_SanitizesMediaAttachmentFilenamesBeforeDispatch()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var unsanitizedPhoto = new MediaAttachmentDto(
            new BlobId("0123456789abcdef"),
            "IMG_20261010_120000.jpg",
            "image/jpeg",
            2048,
            800,
            600,
            null,
            null);

        var cmd = ChatTestFactory.CreateSendMessageCommand(
            channelId: channel.Id,
            authorId: channel.OwnerIdentityId,
            recipientId: channel.RemotePeerId,
            content: "Photo attached",
            attachments: new[] { unsanitizedPhoto });

        var result = await _sut.SendMessageAsync(cmd);
        result.IsSuccess.Should().BeTrue();

        var saved = await _chatRepo.GetByIdAsync(result.Value);
        saved!.Attachments.Should().ContainSingle();
        saved.Attachments[0].FileName.Should().BeNull();
    }

    [Test]
    public async Task SendMessageAsync_GroupChannel_AppendsChannelPayloadToGroupAggregate()
    {
        var group = ChatTestFactory.CreateGroupChannel(_timeProvider);
        await _channelRepo.SaveGroupAsync(group);

        var creatorId = group.Members.First().Id;
        var cmd = ChatTestFactory.CreateSendMessageCommand(
            channelId: group.Id,
            authorId: creatorId,
            recipientId: null,
            content: "Hello group!");

        var result = await _sut.SendMessageAsync(cmd);

        result.IsSuccess.Should().BeTrue();
        var messageId = result.Value;

        // Appended to group domain channel aggregate
        group.Payloads.Should().ContainSingle(p => p.Id == messageId);
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
    public async Task DeleteMessageAsync_Author_MarksTombstone_PurgesLocalBlobs_AndDispatchesTombstoneDto()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var blobId = new BlobId("deadbeef01234567");
        var attachment = new MediaAttachmentDto(blobId, "report.pdf", "application/pdf", 4096, null, null, null, null);

        var sendCmd = ChatTestFactory.CreateSendMessageCommand(
            channelId: channel.Id,
            authorId: channel.OwnerIdentityId,
            recipientId: channel.RemotePeerId,
            content: "To be deleted",
            attachments: new[] { attachment });
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
        _blobStorage.DeletedBlobs.Should().ContainSingle(b => b == blobId);

        var lastSent = _payloadSender.SentPayloads.Last();
        var envelope = _serializer.Deserialize<ChatPayloadEnvelopeDto>(lastSent.Payload).Value;
        envelope.Type.Should().Be(ChatPayloadType.Tombstone);
    }

    [Test]
    public async Task DeleteMessageAsync_GroupAdmin_CanDeleteOtherMemberMessage()
    {
        var group = ChatTestFactory.CreateGroupChannel(_timeProvider);
        var adminId = group.Members.First().Id;
        var memberId = PublicIdentityId.New();
        group.AddMember(adminId, memberId, ChannelRole.Member, _timeProvider);
        await _channelRepo.SaveGroupAsync(group);

        var sendCmd = ChatTestFactory.CreateSendMessageCommand(channelId: group.Id, authorId: memberId, recipientId: null, content: "Spam");
        var sendResult = await _sut.SendMessageAsync(sendCmd);
        var messageId = sendResult.Value;

        // Group creator is admin
        var deleteCmd = new DeleteMessageCommand(
            messageId,
            group.Id,
            adminId,
            null,
            ChatTestFactory.BobDeviceId,
            DeliveryRoute.DirectP2P);

        var deleteResult = await _sut.DeleteMessageAsync(deleteCmd);

        deleteResult.IsSuccess.Should().BeTrue();
        var record = await _chatRepo.GetByIdAsync(messageId);
        record!.IsTombstone.Should().BeTrue();
        record.Content.Should().Be("This message was deleted by a channel administrator");
        record.DeletedBy.Should().Be(adminId);
    }

    [Test]
    public async Task AddReactionAsync_RepeatedCalls_AreIdempotent()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var sendCmd = ChatTestFactory.CreateSendMessageCommand(channelId: channel.Id, authorId: channel.OwnerIdentityId, recipientId: channel.RemotePeerId, content: "Nice work");
        var sendResult = await _sut.SendMessageAsync(sendCmd);
        var messageId = sendResult.Value;

        var emoji = Emoji.Create("\ud83d\ude80").Value;
        var reactCmd = new AddReactionCommand(
            messageId,
            channel.Id,
            channel.OwnerIdentityId,
            channel.RemotePeerId,
            ChatTestFactory.BobDeviceId,
            DeliveryRoute.DirectP2P,
            emoji);

        var first = await _sut.AddReactionAsync(reactCmd);
        var second = await _sut.AddReactionAsync(reactCmd);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();

        var record = await _chatRepo.GetByIdAsync(messageId);
        record!.Reactions.Should().HaveCount(1);
    }

    [Test]
    public async Task MarkAsReadAsync_AdvancesCursor_StartsDisappearingTimer_AndDispatchesReadReceiptDto()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var msgId = PayloadId.New();
        var incomingDisappearingMsg = new ChatMessage(
            msgId,
            channel.Id,
            channel.RemotePeerId,
            _timeProvider.UtcNowUnixMs,
            "View once secret",
            expireAfterSeconds: 60,
            expiresAtUnixMs: null);
        await _chatRepo.AddAsync(incomingDisappearingMsg);

        _timeProvider.Advance(TimeSpan.FromMinutes(5));
        ulong readTimestampMs = _timeProvider.UtcNowUnixMs;

        var readCmd = new MarkAsReadCommand(
            channel.Id,
            channel.OwnerIdentityId,
            msgId,
            channel.RemotePeerId,
            ChatTestFactory.BobDeviceId,
            DeliveryRoute.DirectP2P);

        var result = await _sut.MarkAsReadAsync(readCmd);

        result.IsSuccess.Should().BeTrue();
        var updatedMsg = await _chatRepo.GetByIdAsync(msgId);
        updatedMsg!.ReadAtUnixMs.Should().Be(readTimestampMs);
        updatedMsg.ExpiresAtUnixMs.Should().Be(readTimestampMs + 60_000UL);

        var lastSent = _payloadSender.SentPayloads.Last();
        var envelope = _serializer.Deserialize<ChatPayloadEnvelopeDto>(lastSent.Payload).Value;
        envelope.Type.Should().Be(ChatPayloadType.ReadReceipt);
    }

    [Test]
    public async Task ShareProfileCapabilityAsync_DispatchesProfileCapabilityWithPadding()
    {
        var channel = ChatTestFactory.CreateDirectChannel(_timeProvider);
        await _channelRepo.SaveDirectAsync(channel);

        var profileKey = ProfileKey.Generate();
        var locator = BlindedProfileLocator.FromBytes(new byte[32]);
        var routingToken = BlindedRoutingToken.New();

        var cmd = new ShareProfileCapabilityCommand(
            channel.Id,
            channel.OwnerIdentityId,
            channel.RemotePeerId,
            ChatTestFactory.BobDeviceId,
            profileKey,
            locator,
            routingToken,
            Revision: 1,
            DeliveryRoute.DirectP2P);

        var result = await _sut.ShareProfileCapabilityAsync(cmd);
        result.IsSuccess.Should().BeTrue();

        _payloadSender.SentPayloads.Should().ContainSingle();
        var sent = _payloadSender.SentPayloads[0];
        sent.ChannelId.Should().Be(channel.Id);
        sent.AppId.Should().Be(AppId.Chat);

        var envResult = _serializer.Deserialize<ChatPayloadEnvelopeDto>(sent.Payload);
        envResult.IsSuccess.Should().BeTrue();
        var env = envResult.Value;
        env.Type.Should().Be(ChatPayloadType.ProfileCapability);
        env.Padding.Length.Should().BeGreaterThan(0);

        var dtoResult = _serializer.Deserialize<ProfileCapabilityDto>(env.Data);
        dtoResult.IsSuccess.Should().BeTrue();
        var dto = dtoResult.Value;
        dto.AuthorId.Should().Be(channel.OwnerIdentityId);
        dto.ChannelId.Should().Be(channel.Id);
        dto.Revision.Should().Be(1);
        dto.ProfileKeyBytes.Should().Equal(profileKey.ToArray());
        dto.BlindedLocatorBytes.Should().Equal(locator.ToArray());
        dto.MailboxRoutingTokenBytes.Should().Equal(routingToken.Value.ToByteArray());
    }

    private sealed class FakeBlobStorageService : IBlobStorageService
    {
        public List<BlobId> DeletedBlobs { get; } = new();

        public ValueTask<DomainResult<BlobUploadResult>> UploadBlobAsync(
            BlobUploadRequest request,
            IProgress<TransferProgress>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<DomainResult<BlobDownloadResult>> DownloadBlobAsync(
            BlobDownloadRequest request,
            IProgress<TransferProgress>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public bool IsCachedLocally(BlobId blobId) => true;

        public ValueTask DeleteLocalCacheAsync(BlobId blobId, CancellationToken ct = default)
        {
            DeletedBlobs.Add(blobId);
            return ValueTask.CompletedTask;
        }
    }
}
