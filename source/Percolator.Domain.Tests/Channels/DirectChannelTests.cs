using Percolator.Domain.Channels.Events;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

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
        result.Value.Id.Should().Be(_channelId);
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
}
