using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Tests.TestDoubles;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Tests.Features.GroupInvitations;

[TestFixture]
public class GroupInvitationServiceTests
{
    private InMemoryGroupInvitationRepository _invitationRepo = null!;
    private InMemoryChannelRepository _channelRepo = null!;
    private TestPayloadSender _payloadSender = null!;
    private TestPayloadSerializer _serializer = null!;
    private FakeDateTimeProvider _timeProvider = null!;
    private GroupInvitationService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _invitationRepo = new InMemoryGroupInvitationRepository();
        _channelRepo = new InMemoryChannelRepository();
        _payloadSender = new TestPayloadSender();
        _serializer = new TestPayloadSerializer();
        _timeProvider = new FakeDateTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));

        _sut = new GroupInvitationService(
            _invitationRepo,
            _channelRepo,
            _payloadSender,
            _serializer,
            _timeProvider);
    }

    [Test]
    public async Task ReceiveInvitationAsync_CreatesPendingRecord()
    {
        var groupChannelId = ChannelId.New();
        var inviteId = new InvitationId("invite-100");
        var inviteDto = new GroupInvitationDto(
            inviteId,
            groupChannelId,
            "Security Team",
            ChatTestFactory.BobId,
            "https://relay.percolator.org",
            _timeProvider.UtcNowUnixMs + 3600_000);

        var result = await _sut.ReceiveInvitationAsync(inviteDto);

        result.IsSuccess.Should().BeTrue();
        var saved = await _invitationRepo.GetByIdAsync(inviteId);
        saved.Should().NotBeNull();
        saved!.Status.Should().Be(GroupInvitationStatus.Pending);
        saved.GroupName.Should().Be("Security Team");
    }

    [Test]
    public async Task AcceptInvitationAsync_TransitionsToAccepted_JoinsGroupChannel_AndDispatchesResponse()
    {
        var groupChannelId = ChannelId.New();
        var group = ChatTestFactory.CreateGroupChannel(_timeProvider, groupChannelId, creatorId: ChatTestFactory.BobId);
        await _channelRepo.SaveGroupAsync(group);

        var directChannel = ChatTestFactory.CreateDirectChannel(_timeProvider, ownerId: ChatTestFactory.AliceId, peerId: ChatTestFactory.BobId);
        await _channelRepo.SaveDirectAsync(directChannel);

        var inviteId = new InvitationId("invite-101");
        var inviteDto = new GroupInvitationDto(
            inviteId,
            groupChannelId,
            "Security Team",
            ChatTestFactory.BobId,
            null,
            _timeProvider.UtcNowUnixMs + 3600_000);

        await _sut.ReceiveInvitationAsync(inviteDto);

        var acceptResult = await _sut.AcceptInvitationAsync(
            inviteId,
            ChatTestFactory.AliceId,
            ChatTestFactory.AliceDeviceId,
            directChannel.Id,
            DeliveryRoute.DirectP2P);

        acceptResult.IsSuccess.Should().BeTrue();

        var updated = await _invitationRepo.GetByIdAsync(inviteId);
        updated!.Status.Should().Be(GroupInvitationStatus.Accepted);
        updated.RespondedAtUnixMs.Should().Be(_timeProvider.UtcNowUnixMs);

        // Dispatched response
        _payloadSender.SentPayloads.Should().ContainSingle();
        var sent = _payloadSender.SentPayloads[0];
        sent.ChannelId.Should().Be(directChannel.Id);
        sent.RecipientIdentityId.Should().Be(ChatTestFactory.BobId);

        var envelope = _serializer.Deserialize<ChatPayloadEnvelopeDto>(sent.Payload).Value;
        envelope.Type.Should().Be(ChatPayloadType.GroupInvitationResponse);
    }

    [Test]
    public async Task DeclineInvitationAsync_TransitionsToDeclined_AndDispatchesDeclineResponse()
    {
        var directChannel = ChatTestFactory.CreateDirectChannel(_timeProvider, ownerId: ChatTestFactory.AliceId, peerId: ChatTestFactory.BobId);
        await _channelRepo.SaveDirectAsync(directChannel);

        var inviteId = new InvitationId("invite-102");
        var inviteDto = new GroupInvitationDto(
            inviteId,
            ChannelId.New(),
            "Random Group",
            ChatTestFactory.BobId,
            null,
            _timeProvider.UtcNowUnixMs + 3600_000);

        await _sut.ReceiveInvitationAsync(inviteDto);

        var declineResult = await _sut.DeclineInvitationAsync(
            inviteId,
            ChatTestFactory.AliceId,
            ChatTestFactory.AliceDeviceId,
            directChannel.Id,
            DeliveryRoute.DirectP2P);

        declineResult.IsSuccess.Should().BeTrue();

        var updated = await _invitationRepo.GetByIdAsync(inviteId);
        updated!.Status.Should().Be(GroupInvitationStatus.Declined);

        _payloadSender.SentPayloads.Should().ContainSingle();
        var sent = _payloadSender.SentPayloads[0];
        var envelope = _serializer.Deserialize<ChatPayloadEnvelopeDto>(sent.Payload).Value;
        envelope.Type.Should().Be(ChatPayloadType.GroupInvitationResponse);

        var responseDto = _serializer.Deserialize<GroupInvitationResponseDto>(envelope.Data).Value;
        responseDto.Accepted.Should().BeFalse();
    }

    [Test]
    public async Task AcceptInvitationAsync_WhenExpired_ReturnsInvitationExpiredError()
    {
        var directChannel = ChatTestFactory.CreateDirectChannel(_timeProvider, ownerId: ChatTestFactory.AliceId, peerId: ChatTestFactory.BobId);
        await _channelRepo.SaveDirectAsync(directChannel);

        var expiresAt = _timeProvider.UtcNowUnixMs + 1000;
        var inviteId = new InvitationId("invite-103");
        var inviteDto = new GroupInvitationDto(
            inviteId,
            ChannelId.New(),
            "Expired Group",
            ChatTestFactory.BobId,
            null,
            expiresAt);

        await _sut.ReceiveInvitationAsync(inviteDto);

        // Advance time past expiration
        _timeProvider.Advance(TimeSpan.FromSeconds(5));

        var acceptResult = await _sut.AcceptInvitationAsync(
            inviteId,
            ChatTestFactory.AliceId,
            ChatTestFactory.AliceDeviceId,
            directChannel.Id,
            DeliveryRoute.DirectP2P);

        acceptResult.IsFailure.Should().BeTrue();
        acceptResult.Error.Code.Should().Be("INVITATION_EXPIRED");
    }
}
