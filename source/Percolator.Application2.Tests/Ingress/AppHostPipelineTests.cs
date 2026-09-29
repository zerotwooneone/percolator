using Percolator.Application2.Ingress;
using Percolator.Application2.Tests.TestDoubles;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Application2.Tests.Ingress;

[TestFixture]
public sealed class AppHostPipelineTests
{
    private InMemoryIngressFilterService _filterService = null!;
    private InMemoryRatchetSessionRepository _sessionRepo = null!;
    private ApplicationTestCryptoEngine _cryptoEngine = null!;
    private AppRouter _appRouter = null!;
    private InboundIngressPipeline _pipeline = null!;

    private PublicIdentityId _aliceId;
    private DeviceId _aliceDeviceId;
    private PublicIdentityId _bobId;
    private DeviceId _bobDeviceId;
    private ChannelId _channelId;

    [SetUp]
    public void SetUp()
    {
        _filterService = new InMemoryIngressFilterService();
        _sessionRepo = new InMemoryRatchetSessionRepository();
        _cryptoEngine = new ApplicationTestCryptoEngine();
        _appRouter = new AppRouter();
        _pipeline = new InboundIngressPipeline(_filterService, _sessionRepo, _cryptoEngine, _appRouter);

        _aliceId = PublicIdentityId.New();
        _aliceDeviceId = DeviceId.Primary;
        _bobId = PublicIdentityId.New();
        _bobDeviceId = new DeviceId(2);
        _channelId = ChannelId.New();
    }

    private (DirectRatchetSession AliceSession, DirectRatchetSession BobSession) CreateSessionPair()
    {
        var bobSignedPreKey = _cryptoEngine.GenerateEphemeralKeyPair();

        var bundle = new PreKeyBundle(
            _bobId,
            _bobDeviceId,
            IdentityKey.FromBytes(new byte[32]),
            bobSignedPreKey.PublicKey,
            DeviceLinkProof.FromBytes(new byte[64]));

        var aliceSession = DirectRatchetSession.InitiateOutbound(_aliceId, _aliceDeviceId, bundle, _cryptoEngine).Value!;

        var bobSession = DirectRatchetSession.InitiateInbound(
            _bobId,
            _bobDeviceId,
            _aliceId,
            _aliceDeviceId,
            bobSignedPreKey.PrivateKey,
            aliceSession.LocalEphemeralPublicKey!,
            _cryptoEngine).Value!;

        return (aliceSession, bobSession);
    }

    private byte[] CreateWirePacket(DirectRatchetSession senderSession, AppId appId, byte[] appContent)
    {
        var stepResult = senderSession.StepSendingChain(_cryptoEngine).Value;
        var header = new RatchetWireFrame(senderSession.LocalEphemeralPublicKey!, stepResult.MessageCounter, senderSession.PreviousSendingChainLength);

        byte[] headerBytes = new byte[RatchetWireFrame.HeaderSize];
        header.WriteTo(headerBytes);

        byte[] nonce = new byte[InboundIngressPipeline.NonceSize];
        nonce[0] = 0xAA;

        byte[] inner = new byte[1 + appContent.Length];
        inner[0] = appId.Value;
        Buffer.BlockCopy(appContent, 0, inner, 1, appContent.Length);

        byte[] ciphertext = _cryptoEngine.EncryptAesGcm(stepResult.Key.Span, nonce, inner, headerBytes);
        stepResult.Key.Dispose();

        byte[] wire = new byte[headerBytes.Length + nonce.Length + ciphertext.Length];
        Buffer.BlockCopy(headerBytes, 0, wire, 0, headerBytes.Length);
        Buffer.BlockCopy(nonce, 0, wire, headerBytes.Length, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, wire, headerBytes.Length + nonce.Length, ciphertext.Length);

        return wire;
    }

    [Test]
    public async Task DispatchAsync_WithRegisteredPlugin_InvokesHandlerDirectly()
    {
        // Arrange
        var (_, bobSession) = CreateSessionPair();
        await _sessionRepo.SaveSessionAsync(bobSession);

        var (aliceSessionForSending, _) = CreateSessionPair();
        var chatHandler = new FakeAppPayloadHandler(AppId.Chat);
        _appRouter.RegisterHandler(chatHandler);

        byte[] appContent = "Hello Percolator"u8.ToArray();
        byte[] wirePacket = CreateWirePacket(aliceSessionForSending, AppId.Chat, appContent);

        var envelope = new InboundWireEnvelope(
            _channelId,
            RecipientIdentityId: _bobId,
            SenderIdentityId: _aliceId,
            SenderDeviceId: _aliceDeviceId,
            WirePayload: wirePacket,
            ReceivedAtUtc: DateTimeOffset.UtcNow);

        // Act
        var result = await _pipeline.ProcessInboundAsync(envelope);

        // Assert
        result.IsSuccess.Should().BeTrue();
        chatHandler.HandledContexts.Should().HaveCount(1);
        var handled = chatHandler.HandledContexts[0];
        handled.AppId.Should().Be(AppId.Chat);
        handled.ChannelId.Should().Be(_channelId);
        chatHandler.CopiedPayloads[0].Should().BeEquivalentTo(appContent);
    }

    [Test]
    public async Task DispatchAsync_WithUnregisteredAppId_ReturnsUnknownAppIdFailure()
    {
        // Arrange
        var (_, bobSession) = CreateSessionPair();
        await _sessionRepo.SaveSessionAsync(bobSession);

        var (aliceSessionForSending, _) = CreateSessionPair();
        byte[] appContent = "Unregistered App Payload"u8.ToArray();
        var unregisteredAppId = new AppId(0xFE);
        byte[] wirePacket = CreateWirePacket(aliceSessionForSending, unregisteredAppId, appContent);

        var envelope = new InboundWireEnvelope(
            _channelId,
            RecipientIdentityId: _bobId,
            SenderIdentityId: _aliceId,
            SenderDeviceId: _aliceDeviceId,
            WirePayload: wirePacket,
            ReceivedAtUtc: DateTimeOffset.UtcNow);

        // Act
        var result = await _pipeline.ProcessInboundAsync(envelope);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("UNKNOWN_APP_ID");
    }

    [Test]
    public async Task DispatchAsync_PayloadExceedingSizeLimit_ReturnsPayloadTooLargeError()
    {
        // Arrange
        byte[] oversizedWire = new byte[InboundIngressPipeline.MaxWirePayloadBytes + 1];
        var envelope = new InboundWireEnvelope(
            _channelId,
            RecipientIdentityId: _bobId,
            SenderIdentityId: _aliceId,
            SenderDeviceId: _aliceDeviceId,
            WirePayload: oversizedWire,
            ReceivedAtUtc: DateTimeOffset.UtcNow);

        // Act
        var result = await _pipeline.ProcessInboundAsync(envelope);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("PAYLOAD_TOO_LARGE");
    }

    [Test]
    public async Task DispatchAsync_SenderDisabled_AbortsInIngressFilterPhase()
    {
        // Arrange
        _filterService.BlockIdentity(_aliceId);

        var (_, bobSession) = CreateSessionPair();
        await _sessionRepo.SaveSessionAsync(bobSession);

        var (aliceSessionForSending, _) = CreateSessionPair();
        byte[] wirePacket = CreateWirePacket(aliceSessionForSending, AppId.Chat, "Blocked"u8.ToArray());

        var envelope = new InboundWireEnvelope(
            _channelId,
            RecipientIdentityId: _bobId,
            SenderIdentityId: _aliceId,
            SenderDeviceId: _aliceDeviceId,
            WirePayload: wirePacket,
            ReceivedAtUtc: DateTimeOffset.UtcNow);

        // Act
        var result = await _pipeline.ProcessInboundAsync(envelope);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("IDENTITY_BLOCKED");
    }

    [Test]
    public async Task DispatchAsync_CorruptWireFrame_FailsAssociatedDataValidation()
    {
        // Arrange
        var (_, bobSession) = CreateSessionPair();
        await _sessionRepo.SaveSessionAsync(bobSession);

        var (aliceSessionForSending, _) = CreateSessionPair();
        byte[] wirePacket = CreateWirePacket(aliceSessionForSending, AppId.Chat, "Tampered"u8.ToArray());

        // Tamper with header byte
        wirePacket[0] ^= 0xFF;

        var envelope = new InboundWireEnvelope(
            _channelId,
            RecipientIdentityId: _bobId,
            SenderIdentityId: _aliceId,
            SenderDeviceId: _aliceDeviceId,
            WirePayload: wirePacket,
            ReceivedAtUtc: DateTimeOffset.UtcNow);

        // Act
        var result = await _pipeline.ProcessInboundAsync(envelope);

        // Assert
        result.IsSuccess.Should().BeFalse();
    }

    [Test]
    public async Task DispatchAsync_MissingSession_ReturnsSessionNotFoundFailure()
    {
        // Arrange (Do NOT save session in repository)
        var (aliceSessionForSending, _) = CreateSessionPair();
        byte[] appContent = "Hello to unknown peer"u8.ToArray();
        byte[] wirePacket = CreateWirePacket(aliceSessionForSending, AppId.Chat, appContent);

        var envelope = new InboundWireEnvelope(
            _channelId,
            RecipientIdentityId: _bobId,
            SenderIdentityId: _aliceId,
            SenderDeviceId: _aliceDeviceId,
            WirePayload: wirePacket,
            ReceivedAtUtc: DateTimeOffset.UtcNow);

        // Act
        var result = await _pipeline.ProcessInboundAsync(envelope);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("SESSION_NOT_FOUND");
    }
}
