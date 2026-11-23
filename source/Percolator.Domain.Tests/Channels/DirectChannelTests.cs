using Percolator.Domain.Channels.Events;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Domain.Tests.Channels;

[TestFixture]
public class DirectChannelTests
{
    private FakeDateTimeProvider _timeProvider = null!;
    private PublicIdentityId _ownerId;
    private PublicIdentityId _peerId;
    private ChannelId _channelId;

    [SetUp]
    public void SetUp()
    {
        _timeProvider = new FakeDateTimeProvider(new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero));
        _ownerId = PublicIdentityId.New();
        _peerId = PublicIdentityId.New();
        _channelId = ChannelId.New();
    }

    [Test]
    public void Create_WithValidParameters_ReturnsSuccess()
    {
        var result = DirectChannel.Create(_channelId, _ownerId, _peerId, _timeProvider);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Id.Should().Be(_channelId);
        result.Value.DomainEvents.Should().ContainSingle(e => e is DirectChannelCreatedDomainEvent)
            .Which.Should().BeOfType<DirectChannelCreatedDomainEvent>()
            .Which.Should().Match<DirectChannelCreatedDomainEvent>(e =>
                e.ChannelId == _channelId &&
                e.OwnerIdentityId == _ownerId &&
                e.RemotePeerId == _peerId &&
                e.Status == DirectChannelStatus.Active &&
                e.RelayEndpoint == null);
    }

    [Test]
    public void Create_WithSelfAsPeer_ReturnsSelfChannelNotAllowedError()
    {
        var result = DirectChannel.Create(_channelId, _ownerId, _ownerId, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SELF_CHANNEL_NOT_ALLOWED");
    }

    [Test]
    public void AppendPayload_AppendsPayload_AndUpdatesLastActivityTimestamp()
    {
        var channel = DirectChannel.Create(_channelId, _ownerId, _peerId, _timeProvider).Value;

        _timeProvider.Advance(TimeSpan.FromMinutes(5));
        var payload = new ChannelPayload(
            PayloadId.New(),
            channel.Id,
            _ownerId,
            DeviceId.Primary,
            new byte[] { 42 },
            _timeProvider.UtcNow);

        var result = channel.AppendPayload(payload, _timeProvider);

        result.IsSuccess.Should().BeTrue();
        channel.LastActivityUtc.Should().Be(_timeProvider.UtcNow);
        channel.DomainEvents.Should().ContainSingle(e => e is PayloadAppendedEvent);
    }

    [Test]
    public void AppendPayload_WithDuplicatePayloadId_ReturnsDuplicatePayloadIdError()
    {
        var channel = DirectChannel.Create(_channelId, _ownerId, _peerId, _timeProvider).Value;
        var payloadId = PayloadId.New();
        var payload = new ChannelPayload(payloadId, channel.Id, _ownerId, DeviceId.Primary, new byte[] { 42 }, _timeProvider.UtcNow);

        channel.AppendPayload(payload, _timeProvider).IsSuccess.Should().BeTrue();
        var duplicate = channel.AppendPayload(payload, _timeProvider);

        duplicate.IsFailure.Should().BeTrue();
        duplicate.Error.Code.Should().Be("DUPLICATE_PAYLOAD_ID");
    }

    [Test]
    public void AppendPayload_WithMismatchedChannelId_ReturnsError()
    {
        var channel = DirectChannel.Create(_channelId, _ownerId, _peerId, _timeProvider).Value;
        var wrongChannelId = ChannelId.New();

        var payload = new ChannelPayload(
            PayloadId.New(),
            wrongChannelId,
            _ownerId,
            DeviceId.Primary,
            new byte[] { 42 },
            _timeProvider.UtcNow);

        var result = channel.AppendPayload(payload, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("CHANNEL_MISMATCH");
    }

    [Test]
    public void AppendPayload_WithNonParticipantSender_ReturnsSenderNotParticipantError()
    {
        var channel = DirectChannel.Create(_channelId, _ownerId, _peerId, _timeProvider).Value;
        var strangerId = PublicIdentityId.New();

        var payload = new ChannelPayload(
            PayloadId.New(),
            channel.Id,
            strangerId,
            DeviceId.Primary,
            new byte[] { 42 },
            _timeProvider.UtcNow);

        var result = channel.AppendPayload(payload, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("SENDER_NOT_PARTICIPANT");
    }

    [Test]
    public void Activate_WhenNoRelayEndpointExistsOrPassed_ReturnsMissingRelayEndpointFailure()
    {
        var channel = DirectChannel.Create(_channelId, _ownerId, _peerId, _timeProvider, DirectChannelStatus.PendingAcceptance, relayEndpoint: null).Value;

        var result = channel.Activate(_timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("MISSING_RELAY_ENDPOINT");
    }

    [Test]
    public void Activate_WithRelayEndpoint_TransitionsToActive_EmitsDirectChannelActivatedDomainEvent()
    {
        var channel = DirectChannel.Create(_channelId, _ownerId, _peerId, _timeProvider, DirectChannelStatus.PendingAcceptance, relayEndpoint: null).Value;
        channel.ClearDomainEvents();

        var relayEndpoint = Percolator.Domain.Delivery.ValueObjects.RelayEndpoint.Parse("percolator://relay.example.com:5100");
        _timeProvider.Advance(TimeSpan.FromMinutes(2));
        var result = channel.Activate(_timeProvider, relayEndpoint);

        result.IsSuccess.Should().BeTrue();
        channel.Status.Should().Be(DirectChannelStatus.Active);
        channel.RelayEndpoint.Should().Be(relayEndpoint);
        channel.LastActivityUtc.Should().Be(_timeProvider.UtcNow);
        channel.DomainEvents.Should().ContainSingle(e => e is DirectChannelActivatedDomainEvent)
            .Which.Should().BeOfType<DirectChannelActivatedDomainEvent>()
            .Which.Should().Match<DirectChannelActivatedDomainEvent>(e =>
                e.ChannelId == _channelId &&
                e.OwnerIdentityId == _ownerId &&
                e.RemotePeerId == _peerId &&
                e.RelayEndpoint == relayEndpoint &&
                e.OccurredOnUtc == _timeProvider.UtcNow);
    }

    [Test]
    public void UpdateRelayEndpoint_NullEndpoint_Fails()
    {
        var channel = DirectChannel.Create(_channelId, _ownerId, _peerId, _timeProvider).Value;

        var result = channel.UpdateRelayEndpoint(null!, _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("MISSING_RELAY_ENDPOINT");
    }

    [Test]
    public void UpdateRelayEndpoint_ValidEndpoint_UpdatesEndpointAndActivityUtc()
    {
        var channel = DirectChannel.Create(_channelId, _ownerId, _peerId, _timeProvider).Value;
        var relayEndpoint = Percolator.Domain.Delivery.ValueObjects.RelayEndpoint.Parse("percolator://relay.example.com:5100");

        _timeProvider.Advance(TimeSpan.FromMinutes(10));
        var result = channel.UpdateRelayEndpoint(relayEndpoint, _timeProvider);

        result.IsSuccess.Should().BeTrue();
        channel.RelayEndpoint.Should().Be(relayEndpoint);
        channel.LastActivityUtc.Should().Be(_timeProvider.UtcNow);
    }

    [Test]
    public void Create_WithUndefinedStatus_ReturnsInvalidChannelStatusFailure()
    {
        var result = DirectChannel.Create(_channelId, _ownerId, _peerId, _timeProvider, (DirectChannelStatus)999);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_CHANNEL_STATUS");
    }

    [Test]
    public void Rehydrate_WithUndefinedStatus_ThrowsArgumentOutOfRangeException()
    {
        var act = () => DirectChannel.Rehydrate(
            _channelId,
            _ownerId,
            _peerId,
            _timeProvider.UtcNow,
            _timeProvider.UtcNow,
            status: (DirectChannelStatus)999);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
