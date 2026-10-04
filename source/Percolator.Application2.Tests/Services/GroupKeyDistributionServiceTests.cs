using System.Buffers.Binary;
using Percolator.Application2.Services;
using Percolator.Application2.Tests.TestDoubles;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Application2.Tests.Services;

[TestFixture]
public sealed class GroupKeyDistributionServiceTests
{
    private InMemoryGroupSenderKeyRepository _senderRepo = null!;
    private InMemoryGroupReceiverSessionRepository _receiverRepo = null!;
    private FakePayloadSender _payloadSender = null!;
    private ApplicationTestCryptoEngine _cryptoEngine = null!;
    private GroupKeyDistributionService _service = null!;

    private ChannelId _channelId;
    private PublicIdentityId _senderId;
    private DeviceId _senderDeviceId;
    private PublicIdentityId _recipient1;
    private PublicIdentityId _recipient2;

    [SetUp]
    public void SetUp()
    {
        _senderRepo = new InMemoryGroupSenderKeyRepository();
        _receiverRepo = new InMemoryGroupReceiverSessionRepository();
        _payloadSender = new FakePayloadSender();
        _cryptoEngine = new ApplicationTestCryptoEngine();

        _service = new GroupKeyDistributionService(
            _senderRepo,
            _receiverRepo,
            _payloadSender,
            _cryptoEngine);

        _channelId = ChannelId.New();
        _senderId = PublicIdentityId.New();
        _senderDeviceId = DeviceId.Primary;
        _recipient1 = PublicIdentityId.New();
        _recipient2 = PublicIdentityId.New();
    }

    [Test]
    public async Task DistributeSenderKeyAsync_WhenNoRatchetExists_GeneratesNewRatchetAndDispatchesToRecipients()
    {
        // Arrange
        var recipients = new[] { _senderId, _recipient1, _recipient2 };

        // Act
        var result = await _service.DistributeSenderKeyAsync(
            _channelId,
            _senderId,
            _senderDeviceId,
            recipients);

        // Assert
        result.IsSuccess.Should().BeTrue();

        // 1. Ratchet saved in repo
        var savedRatchet = await _senderRepo.GetSenderKeyRatchetAsync(_channelId, _senderId, _senderDeviceId);
        savedRatchet.Should().NotBeNull();
        savedRatchet!.KeyId.Should().Be(1);
        savedRatchet.Iteration.Should().Be(0);

        // 2. Sent to 2 recipients (excluding self)
        _payloadSender.SentPayloads.Should().HaveCount(2);

        var p1 = _payloadSender.SentPayloads[0];
        p1.SenderIdentityId.Should().Be(_senderId);
        p1.RecipientIdentityId.Should().Be(_recipient1);
        p1.AppId.Should().Be(AppId.SystemControl);
        p1.Payload.Length.Should().Be(GroupKeyDistributionService.DistributionPayloadLength);

        var p2 = _payloadSender.SentPayloads[1];
        p2.RecipientIdentityId.Should().Be(_recipient2);
        p2.AppId.Should().Be(AppId.SystemControl);

        // Verify payload contents
        var span = p1.Payload.Span;
        span[0].Should().Be(0x01); // MessageTypeSenderKeyDistribution
        var channelBytes = new byte[16];
        _channelId.TryWriteBytes(channelBytes);
        span.Slice(1, 16).ToArray().Should().BeEquivalentTo(channelBytes);

        var keyId = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(17, 4));
        keyId.Should().Be(1);
        var iteration = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(21, 4));
        iteration.Should().Be(0);
    }

    [Test]
    public async Task DistributeSenderKeyAsync_WhenRatchetExists_UsesExistingRatchet()
    {
        // Arrange: Pre-populate existing ratchet
        using var chainKey = ChainKey.FromSpan(new byte[32]);
        var (signingPriv, signingPub) = _cryptoEngine.GenerateEphemeralKeyPair();
        var authorKey = IdentityKey.FromSpan(signingPub.Span);

        var existingRatchet = new GroupSenderKeyRatchet(
            _channelId,
            _senderId,
            _senderDeviceId,
            chainKey,
            initialIteration: 5,
            keyId: 42,
            signingPrivateKey: signingPriv,
            authorSigningPublicKey: authorKey);

        await _senderRepo.SaveSenderKeyRatchetAsync(existingRatchet);

        // Act
        var result = await _service.DistributeSenderKeyAsync(
            _channelId,
            _senderId,
            _senderDeviceId,
            [_recipient1]);

        // Assert
        result.IsSuccess.Should().BeTrue();
        _payloadSender.SentPayloads.Should().HaveCount(1);
        var payload = _payloadSender.SentPayloads[0].Payload;

        var keyId = BinaryPrimitives.ReadUInt32BigEndian(payload.Span.Slice(17, 4));
        keyId.Should().Be(42);

        var iteration = BinaryPrimitives.ReadUInt32BigEndian(payload.Span.Slice(21, 4));
        iteration.Should().Be(5);
    }

    [Test]
    public async Task ProcessInboundDistributionAsync_ValidPayload_SavesReceiverSession()
    {
        // Arrange
        byte[] payload = new byte[GroupKeyDistributionService.DistributionPayloadLength];
        payload[0] = 0x01;
        _channelId.TryWriteBytes(payload.AsSpan(1, 16));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(17, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(21, 4), 0);

        byte[] chainBytes = new byte[32];
        chainBytes[0] = 0x42;
        chainBytes.CopyTo(payload.AsSpan(25, 32));

        var (_, signingPub) = _cryptoEngine.GenerateEphemeralKeyPair();
        signingPub.Span.CopyTo(payload.AsSpan(57, 32));

        var inboundContext = new InboundPayloadContext(
            _channelId,
            _senderId,
            _senderDeviceId,
            AppId.SystemControl,
            payload,
            DateTimeOffset.UtcNow);

        // Act
        var result = await _service.ProcessInboundDistributionAsync(inboundContext);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var session = await _receiverRepo.GetReceiverSessionAsync(_channelId, _senderId, _senderDeviceId);
        session.Should().NotBeNull();
        session!.AuthorSigningKey!.Span.ToArray().Should().BeEquivalentTo(signingPub.Span.ToArray());
    }

    [Test]
    public async Task ProcessInboundDistributionAsync_PayloadTooShort_ReturnsFailure()
    {
        // Arrange
        byte[] shortPayload = new byte[10];
        var inboundContext = new InboundPayloadContext(
            _channelId,
            _senderId,
            _senderDeviceId,
            AppId.SystemControl,
            shortPayload,
            DateTimeOffset.UtcNow);

        // Act
        var result = await _service.ProcessInboundDistributionAsync(inboundContext);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("INVALID_PAYLOAD_SIZE");
    }

    [Test]
    public async Task ProcessInboundDistributionAsync_UnknownMessageType_ReturnsFailure()
    {
        // Arrange
        byte[] payload = new byte[GroupKeyDistributionService.DistributionPayloadLength];
        payload[0] = 0x99; // unknown message type

        var inboundContext = new InboundPayloadContext(
            _channelId,
            _senderId,
            _senderDeviceId,
            AppId.SystemControl,
            payload,
            DateTimeOffset.UtcNow);

        // Act
        var result = await _service.ProcessInboundDistributionAsync(inboundContext);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("UNSUPPORTED_SYSTEM_MESSAGE");
    }
}
