using System.Buffers.Binary;
using Percolator.Application2.Handshake;
using Percolator.Application2.Ingress;
using Percolator.Application2.Ports;
using Percolator.Application2.Profiles;
using Percolator.Application2.Services;
using Percolator.Application2.Tests.TestDoubles;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.Model;
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
    private InMemoryGroupReceiverSessionRepository _groupReceiverRepo = null!;
    private InMemoryGroupSenderKeyRepository _senderKeyRepo = null!;
    private InMemoryLocalIdentityKeyStore _identityKeyStore = null!;
    private InMemoryPrivatePreKeyStore _preKeyStore = null!;
    private InMemoryPeerContactRepository _contactRepo = null!;
    private InMemoryPendingHandshakeRepository _pendingHandshakeRepo = null!;
    private FakeHandshakeReplayFilter _replayFilter = null!;
    private ContactRequestCoordinator _coordinator = null!;
    private InMemoryOutboxRepository _outboxRepo = null!;
    private TestDateTimeProvider _timeProvider = null!;
    private ApplicationTestCryptoEngine _cryptoEngine = null!;
    private AppRouter _appRouter = null!;
    private HandshakeService _handshakeService = null!;
    private NoOpChannelLockService _channelLockService = null!;
    private FakePayloadSender _payloadSender = null!;
    private GroupKeyDistributionService _groupKeyDistributionService = null!;
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
        _groupReceiverRepo = new InMemoryGroupReceiverSessionRepository();
        _senderKeyRepo = new InMemoryGroupSenderKeyRepository();
        _identityKeyStore = new InMemoryLocalIdentityKeyStore();
        _preKeyStore = new InMemoryPrivatePreKeyStore();
        _contactRepo = new InMemoryPeerContactRepository();
        _pendingHandshakeRepo = new InMemoryPendingHandshakeRepository();
        _replayFilter = new FakeHandshakeReplayFilter();
        _timeProvider = new TestDateTimeProvider();
        _coordinator = new ContactRequestCoordinator(_contactRepo, _timeProvider);
        _outboxRepo = new InMemoryOutboxRepository();
        _cryptoEngine = new ApplicationTestCryptoEngine();
        _appRouter = new AppRouter();
        _channelLockService = new NoOpChannelLockService();
        _payloadSender = new FakePayloadSender();

        _handshakeService = new HandshakeService(
            _identityKeyStore,
            _preKeyStore,
            _contactRepo,
            _coordinator,
            _sessionRepo,
            _pendingHandshakeRepo,
            _replayFilter,
            _outboxRepo,
            _cryptoEngine,
            _timeProvider,
            _appRouter);

        _groupKeyDistributionService = new GroupKeyDistributionService(
            _senderKeyRepo,
            _groupReceiverRepo,
            _payloadSender,
            _cryptoEngine);

        _pipeline = new InboundIngressPipeline(
            _filterService,
            _sessionRepo,
            _groupReceiverRepo,
            _handshakeService,
            _cryptoEngine,
            _appRouter,
            _channelLockService,
            _groupKeyDistributionService);

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

    private InboundDirectEnvelope CreateDirectEnvelope(DirectRatchetSession senderSession, AppId appId, byte[] appContent)
    {
        var stepResult = senderSession.StepSendingChain(_cryptoEngine).Value;
        var header = new RatchetHeader(
            senderSession.LocalEphemeralPublicKey!,
            stepResult.MessageCounter,
            senderSession.PreviousSendingChainLength);

        byte[] nonce = new byte[12];
        nonce[0] = 0xAA;

        byte[] inner = new byte[1 + appContent.Length];
        inner[0] = appId.Value;
        Buffer.BlockCopy(appContent, 0, inner, 1, appContent.Length);

        byte[] ciphertext = _cryptoEngine.EncryptAesGcm(stepResult.Key.Span, nonce, inner, header.EphemeralPublicKey.Span);
        stepResult.Key.Dispose();

        return new InboundDirectEnvelope(
            _channelId,
            _bobId,
            _aliceId,
            _aliceDeviceId,
            header,
            nonce,
            ciphertext,
            DateTimeOffset.UtcNow);
    }

    [Test]
    public async Task ProcessInboundAsync_DirectMessage_WithRegisteredPlugin_InvokesHandlerDirectly()
    {
        // Arrange
        var (_, bobSession) = CreateSessionPair();
        await _sessionRepo.SaveSessionAsync(bobSession);

        var (aliceSessionForSending, _) = CreateSessionPair();
        var chatHandler = new FakeAppPayloadHandler(AppId.Chat);
        _appRouter.RegisterHandler(chatHandler);

        byte[] appContent = "Hello Percolator"u8.ToArray();
        var envelope = CreateDirectEnvelope(aliceSessionForSending, AppId.Chat, appContent);

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
    public async Task ProcessInboundAsync_DirectMessage_WithUnregisteredAppId_ReturnsUnknownAppIdFailure()
    {
        // Arrange
        var (_, bobSession) = CreateSessionPair();
        await _sessionRepo.SaveSessionAsync(bobSession);

        var (aliceSessionForSending, _) = CreateSessionPair();
        byte[] appContent = "Unregistered App Payload"u8.ToArray();
        var unregisteredAppId = new AppId(0xFE);
        var envelope = CreateDirectEnvelope(aliceSessionForSending, unregisteredAppId, appContent);

        // Act
        var result = await _pipeline.ProcessInboundAsync(envelope);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("UNKNOWN_APP_ID");
    }

    [Test]
    public async Task ProcessInboundAsync_PayloadExceedingSizeLimit_ReturnsPayloadTooLargeError()
    {
        // Arrange
        byte[] oversized = new byte[InboundIngressPipeline.MaxPayloadBytes + 1];
        var envelope = new InboundDirectEnvelope(
            _channelId,
            _bobId,
            _aliceId,
            _aliceDeviceId,
            new RatchetHeader(DhPublicKey.FromBytes(new byte[32]), 0, 0),
            new byte[12],
            oversized,
            DateTimeOffset.UtcNow);

        // Act
        var result = await _pipeline.ProcessInboundAsync(envelope);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("PAYLOAD_TOO_LARGE");
    }

    [Test]
    public async Task ProcessInboundAsync_SenderDisabled_AbortsInIngressFilterPhase()
    {
        // Arrange
        _filterService.BlockIdentity(_aliceId);

        var (_, bobSession) = CreateSessionPair();
        await _sessionRepo.SaveSessionAsync(bobSession);

        var (aliceSessionForSending, _) = CreateSessionPair();
        var envelope = CreateDirectEnvelope(aliceSessionForSending, AppId.Chat, "Blocked"u8.ToArray());

        // Act
        var result = await _pipeline.ProcessInboundAsync(envelope);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("IDENTITY_BLOCKED");
    }

    [Test]
    public async Task ProcessInboundAsync_TamperedCiphertext_FailsAuthentication()
    {
        // Arrange
        var (_, bobSession) = CreateSessionPair();
        await _sessionRepo.SaveSessionAsync(bobSession);

        var (aliceSessionForSending, _) = CreateSessionPair();
        var validEnvelope = CreateDirectEnvelope(aliceSessionForSending, AppId.Chat, "Tampered"u8.ToArray());

        byte[] tamperedCiphertext = validEnvelope.Ciphertext.ToArray();
        tamperedCiphertext[0] ^= 0xFF;

        var envelope = new InboundDirectEnvelope(
            validEnvelope.ChannelId,
            validEnvelope.RecipientIdentityId,
            validEnvelope.SenderIdentityId,
            validEnvelope.SenderDeviceId,
            validEnvelope.Header,
            validEnvelope.Nonce,
            tamperedCiphertext,
            validEnvelope.ReceivedAtUtc);

        // Act
        var result = await _pipeline.ProcessInboundAsync(envelope);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("DECRYPTION_FAILED");
    }

    [Test]
    public async Task ProcessInboundAsync_MissingSession_ReturnsSessionNotFoundFailure()
    {
        // Arrange (Do NOT save session in repository)
        var (aliceSessionForSending, _) = CreateSessionPair();
        var envelope = CreateDirectEnvelope(aliceSessionForSending, AppId.Chat, "Unknown peer"u8.ToArray());

        // Act
        var result = await _pipeline.ProcessInboundAsync(envelope);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("SESSION_NOT_FOUND");
    }

    [Test]
    public async Task ProcessInboundAsync_GroupMessage_VerifiesAuthorSignatureAndDispatches()
    {
        // Arrange
        using var chainKey = ChainKey.FromSpan(new byte[32]);
        var (authorPriv, authorPub) = _cryptoEngine.GenerateEphemeralKeyPair();
        var authorSigningKey = IdentityKey.FromSpan(authorPub.Span);

        var receiverSession = new GroupReceiverSession(
            _channelId,
            _aliceId,
            _aliceDeviceId,
            chainKey,
            initialIteration: 0,
            authorSigningKey: authorSigningKey);

        await _groupReceiverRepo.SaveReceiverSessionAsync(receiverSession);

        var chatHandler = new FakeAppPayloadHandler(AppId.Chat);
        _appRouter.RegisterHandler(chatHandler);

        // Encrypt inner group payload with message key derived at iteration 0
        using var authorChain = ChainKey.FromSpan(new byte[32]);
        var (nextChain, messageKey) = _cryptoEngine.StepRatchet(authorChain);
        nextChain.Dispose();

        byte[] appContent = "Group Chat Payload"u8.ToArray();
        byte[] inner = new byte[1 + appContent.Length];
        inner[0] = AppId.Chat.Value;
        Buffer.BlockCopy(appContent, 0, inner, 1, appContent.Length);

        byte[] nonce = new byte[12];
        byte[] ciphertext = _cryptoEngine.EncryptAesGcm(messageKey.Span, nonce, inner, ReadOnlySpan<byte>.Empty);
        messageKey.Dispose();

        byte[] signature = _cryptoEngine.SignEd25519(authorPriv.Span, ciphertext);

        var envelope = new InboundGroupEnvelope(
            _channelId,
            _bobId,
            _aliceId,
            _aliceDeviceId,
            Iteration: 0,
            Ciphertext: ciphertext,
            Signature: signature,
            ReceivedAtUtc: DateTimeOffset.UtcNow);

        // Act
        var result = await _pipeline.ProcessInboundAsync(envelope);

        // Assert
        result.IsSuccess.Should().BeTrue();
        chatHandler.HandledContexts.Should().HaveCount(1);
        chatHandler.CopiedPayloads[0].Should().BeEquivalentTo(appContent);
    }

    [Test]
    public async Task ProcessInboundAsync_SystemControlSenderKeyDistribution_InstallsGroupReceiverSession()
    {
        // Arrange: Alice and Bob have an established pairwise direct session
        var (_, bobSession) = CreateSessionPair();
        await _sessionRepo.SaveSessionAsync(bobSession);

        var (aliceSessionForSending, _) = CreateSessionPair();

        // Alice prepares a group sender key distribution payload
        byte[] distPayload = new byte[GroupKeyDistributionService.DistributionPayloadLength];
        distPayload[0] = 0x01; // MessageTypeSenderKeyDistribution
        _channelId.TryWriteBytes(distPayload.AsSpan(1, 16));
        BinaryPrimitives.WriteUInt32BigEndian(distPayload.AsSpan(17, 4), 1); // KeyId = 1
        BinaryPrimitives.WriteUInt32BigEndian(distPayload.AsSpan(21, 4), 0); // Iteration = 0

        byte[] chainBytes = new byte[32];
        chainBytes[0] = 0x77;
        chainBytes.CopyTo(distPayload.AsSpan(25, 32));

        var (authorPriv, authorPub) = _cryptoEngine.GenerateEphemeralKeyPair();
        authorPub.Span.CopyTo(distPayload.AsSpan(57, 32));

        // Package as direct envelope targeting AppId.SystemControl
        var envelope = CreateDirectEnvelope(aliceSessionForSending, AppId.SystemControl, distPayload);

        // Act: Bob receives the direct envelope
        var result = await _pipeline.ProcessInboundAsync(envelope);

        // Assert: Process succeeds
        result.IsSuccess.Should().BeTrue();

        // Group receiver session was successfully saved in repository for Alice on _channelId!
        var installedSession = await _groupReceiverRepo.GetReceiverSessionAsync(_channelId, _aliceId, _aliceDeviceId);
        installedSession.Should().NotBeNull();
        installedSession!.ChannelId.Should().Be(_channelId);
        installedSession.AuthorId.Should().Be(_aliceId);
        installedSession.AuthorSigningKey!.Span.ToArray().Should().BeEquivalentTo(authorPub.Span.ToArray());
    }
}
