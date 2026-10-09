using Percolator.Application2.Handshake;
using Percolator.Application2.Ingress;
using Percolator.Application2.Ports;
using Percolator.Application2.Profiles;
using Percolator.Application2.Tests.TestDoubles;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Application2.Tests.Handshake;

[TestFixture]
public sealed class HandshakeServiceTests
{
    private InMemoryLocalIdentityKeyStore _identityKeyStore = null!;
    private InMemoryPrivatePreKeyStore _preKeyStore = null!;
    private InMemoryPeerContactRepository _contactRepo = null!;
    private ContactRequestCoordinator _coordinator = null!;
    private InMemoryRatchetSessionRepository _sessionRepo = null!;
    private InMemoryPendingHandshakeRepository _pendingHandshakeRepo = null!;
    private FakeHandshakeReplayFilter _replayFilter = null!;
    private InMemoryOutboxRepository _outboxRepo = null!;
    private ApplicationTestCryptoEngine _cryptoEngine = null!;
    private TestDateTimeProvider _timeProvider = null!;
    private AppRouter _appRouter = null!;
    private HandshakeService _handshakeService = null!;

    private PublicIdentityId _aliceId;
    private DeviceId _aliceDeviceId;
    private byte[] _alicePrivKey = null!;
    private IdentityKey _aliceIdentityKey = null!;
    private PublicIdentityId _bobId;
    private DeviceId _bobDeviceId;
    private byte[] _bobPrivKey = null!;
    private IdentityKey _bobIdentityKey = null!;

    [SetUp]
    public void SetUp()
    {
        _identityKeyStore = new InMemoryLocalIdentityKeyStore();
        _preKeyStore = new InMemoryPrivatePreKeyStore();
        _contactRepo = new InMemoryPeerContactRepository();
        _timeProvider = new TestDateTimeProvider();
        _coordinator = new ContactRequestCoordinator(_contactRepo, _timeProvider);
        _sessionRepo = new InMemoryRatchetSessionRepository();
        _pendingHandshakeRepo = new InMemoryPendingHandshakeRepository();
        _replayFilter = new FakeHandshakeReplayFilter();
        _outboxRepo = new InMemoryOutboxRepository();
        _cryptoEngine = new ApplicationTestCryptoEngine();
        _appRouter = new AppRouter();

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

        _aliceId = PublicIdentityId.New();
        _aliceDeviceId = DeviceId.Primary;
        _alicePrivKey = new byte[32];
        _alicePrivKey[0] = 0xAA;
        byte[] alicePub = new byte[32];
        alicePub[0] = 0x0A;
        _aliceIdentityKey = IdentityKey.FromSpan(alicePub);
        _identityKeyStore.RegisterKey(_aliceId, _alicePrivKey, _aliceIdentityKey);

        _bobId = PublicIdentityId.New();
        _bobDeviceId = DeviceId.Primary;
        _bobPrivKey = new byte[32];
        _bobPrivKey[0] = 0xBB;
        byte[] bobPub = new byte[32];
        bobPub[0] = 0x0B;
        _bobIdentityKey = IdentityKey.FromSpan(bobPub);
        _identityKeyStore.RegisterKey(_bobId, _bobPrivKey, _bobIdentityKey);
    }

    private PreKeyBundle CreateBobPreKeyBundle(out EphemeralPrivateKey bobSpkPriv, out EphemeralPrivateKey bobOpkPriv)
    {
        var spkPair = _cryptoEngine.GenerateEphemeralKeyPair();
        bobSpkPriv = spkPair.PrivateKey;

        var opkPair = _cryptoEngine.GenerateEphemeralKeyPair();
        bobOpkPriv = opkPair.PrivateKey;

        var spkSig = _cryptoEngine.SignEd25519(_bobPrivKey, spkPair.PublicKey.Span);

        return new PreKeyBundle(
            _bobId,
            _bobDeviceId,
            _bobIdentityKey,
            spkPair.PublicKey,
            DeviceLinkProof.FromBytes(spkSig),
            OneTimePreKeyId: 42,
            OneTimePreKey: opkPair.PublicKey);
    }

    [Test]
    public async Task InitiateHandshakeAsync_CreatesOutboundSession_AndEnqueuesOutboxJob()
    {
        // Arrange
        var bobBundle = CreateBobPreKeyBundle(out _, out _);

        // Act
        var result = await _handshakeService.InitiateHandshakeAsync(
            _aliceId,
            _aliceDeviceId,
            bobBundle,
            AppId.Chat,
            "Initial Hello"u8.ToArray());

        // Assert
        result.IsSuccess.Should().BeTrue();
        var session = result.Value!;
        session.OwnerIdentityId.Should().Be(_aliceId);
        session.RemotePeerId.Should().Be(_bobId);

        var savedSession = await _sessionRepo.GetSessionAsync(_aliceId, _bobId, _bobDeviceId);
        savedSession.Should().NotBeNull();

        _outboxRepo.AllJobs.Should().HaveCount(1);
        var job = _outboxRepo.AllJobs.First();
        job.OwnerIdentityId.Should().Be(_aliceId);
        job.RecipientIdentityId.Should().Be(_bobId);
    }

    [Test]
    public async Task ReceiveInvitationAsync_WhenSenderBlocked_ReturnsFailure()
    {
        // Arrange
        var contact = new PeerContact(
            _bobId,
            _aliceId,
            ContactNickname.Create("Alice", _aliceId),
            PeerTrustLevel.Verified,
            _timeProvider.UtcNow,
            _aliceIdentityKey);
        contact.Block();
        await _contactRepo.SaveAsync(contact);

        var (_, ephPub) = _cryptoEngine.GenerateEphemeralKeyPair();
        var envelope = new InboundHandshakeEnvelope(
            RecipientIdentityId: _bobId,
            SenderIdentityId: _aliceId,
            SenderDeviceId: _aliceDeviceId,
            SenderIdentityKey: _aliceIdentityKey,
            SenderEphemeralKey: ephPub,
            SignedPreKeyId: 1,
            OneTimePreKeyId: null,
            EncryptedPayload: ReadOnlyMemory<byte>.Empty,
            ReceivedAtUtc: DateTimeOffset.UtcNow);

        // Act
        var result = await _handshakeService.ReceiveInvitationAsync(envelope);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("CONTACT_BLOCKED");
    }

    [Test]
    public async Task ReceiveInvitationAsync_WhenEphemeralKeyReplayed_ReturnsFailure()
    {
        // Arrange
        var (_, ephPub) = _cryptoEngine.GenerateEphemeralKeyPair();
        var envelope = new InboundHandshakeEnvelope(
            RecipientIdentityId: _bobId,
            SenderIdentityId: _aliceId,
            SenderDeviceId: _aliceDeviceId,
            SenderIdentityKey: _aliceIdentityKey,
            SenderEphemeralKey: ephPub,
            SignedPreKeyId: 1,
            OneTimePreKeyId: null,
            EncryptedPayload: ReadOnlyMemory<byte>.Empty,
            ReceivedAtUtc: DateTimeOffset.UtcNow);

        // First attempt succeeds (deferred to coordinator)
        var firstResult = await _handshakeService.ReceiveInvitationAsync(envelope);
        firstResult.IsSuccess.Should().BeTrue();

        // Second attempt with exact same ephemeral key is rejected by replay filter
        var replayResult = await _handshakeService.ReceiveInvitationAsync(envelope);
        replayResult.IsSuccess.Should().BeFalse();
        replayResult.Error!.Code.Should().Be("HANDSHAKE_REPLAY_DETECTED");
    }

    [Test]
    public async Task ReceiveInvitationAsync_WhenContactNotFound_DefersToCoordinator()
    {
        // Arrange (no contact exists)
        var (_, ephPub) = _cryptoEngine.GenerateEphemeralKeyPair();
        var envelope = new InboundHandshakeEnvelope(
            RecipientIdentityId: _bobId,
            SenderIdentityId: _aliceId,
            SenderDeviceId: _aliceDeviceId,
            SenderIdentityKey: _aliceIdentityKey,
            SenderEphemeralKey: ephPub,
            SignedPreKeyId: 1,
            OneTimePreKeyId: null,
            EncryptedPayload: ReadOnlyMemory<byte>.Empty,
            ReceivedAtUtc: DateTimeOffset.UtcNow);

        // Act
        var result = await _handshakeService.ReceiveInvitationAsync(envelope);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var savedContact = await _contactRepo.GetByPeerIdAsync(_bobId, _aliceId);
        savedContact.Should().NotBeNull();
        savedContact!.State.Should().Be(ContactState.PendingApproval);

        // Session should NOT be created yet
        var session = await _sessionRepo.GetSessionAsync(_bobId, _aliceId, _aliceDeviceId);
        session.Should().BeNull();
    }

    [Test]
    public async Task ReceiveInvitationAsync_WhenContactActive_DerivesSessionAndDispatchesMessage()
    {
        // Arrange
        var contact = new PeerContact(
            _bobId,
            _aliceId,
            ContactNickname.Create("Alice", _aliceId),
            PeerTrustLevel.Verified,
            _timeProvider.UtcNow,
            _aliceIdentityKey);
        await _contactRepo.SaveAsync(contact);

        var bobBundle = CreateBobPreKeyBundle(out var bobSpkPriv, out var bobOpkPriv);
        await _preKeyStore.StoreSignedPreKeyPrivateAsync(_bobId, DeviceId.Primary, bobSpkPriv);
        await _preKeyStore.StoreOneTimePreKeysPrivateAsync(_bobId, DeviceId.Primary, [(42, bobOpkPriv)]);

        var chatHandler = new FakeAppPayloadHandler(AppId.Chat);
        _appRouter.RegisterHandler(chatHandler);

        // Alice initiates X3DH
        var x3dhInitResult = X3dhAgreement.Initiate(_alicePrivKey, _aliceIdentityKey, bobBundle, _cryptoEngine).Value!;

        // Alice derives outbound session and encrypts initial message
        var aliceSession = DirectRatchetSession.CreateFromX3dhInitiator(
            _aliceId,
            _aliceDeviceId,
            _bobId,
            _bobDeviceId,
            x3dhInitResult,
            bobBundle.SignedPreKey,
            _cryptoEngine).Value!;

        var (counter, messageKey, _) = aliceSession.StepSendingChain(_cryptoEngine).Value;
        byte[] payloadData = "Piggybacked Hello"u8.ToArray();
        byte[] inner = new byte[1 + payloadData.Length];
        inner[0] = AppId.Chat.Value;
        Buffer.BlockCopy(payloadData, 0, inner, 1, payloadData.Length);

        byte[] nonce = new byte[12];
        byte[] initialCiphertext = _cryptoEngine.EncryptAesGcm(messageKey.Span, nonce, inner, ReadOnlySpan<byte>.Empty);
        messageKey.Dispose();

        var envelope = new InboundHandshakeEnvelope(
            RecipientIdentityId: _bobId,
            SenderIdentityId: _aliceId,
            SenderDeviceId: _aliceDeviceId,
            SenderIdentityKey: _aliceIdentityKey,
            SenderEphemeralKey: x3dhInitResult.EphemeralPublicKey,
            SignedPreKeyId: 1,
            OneTimePreKeyId: 42,
            EncryptedPayload: initialCiphertext,
            ReceivedAtUtc: DateTimeOffset.UtcNow);

        // Act
        var result = await _handshakeService.ReceiveInvitationAsync(envelope);

        // Assert
        result.IsSuccess.Should().BeTrue();

        var bobSession = await _sessionRepo.GetSessionAsync(_bobId, _aliceId, _aliceDeviceId);
        bobSession.Should().NotBeNull();

        chatHandler.HandledContexts.Should().HaveCount(1);
        chatHandler.CopiedPayloads[0].Should().BeEquivalentTo(payloadData);
    }

    [Test]
    public async Task ReceiveInvitationAsync_WhenPendingApproval_PreservesEnvelopeAndCompletesUponApproval()
    {
        // Bob pre-keys setup
        var bobBundle = CreateBobPreKeyBundle(out var bobSpkPriv, out var bobOpkPriv);
        await _preKeyStore.StoreSignedPreKeyPrivateAsync(_bobId, DeviceId.Primary, bobSpkPriv);
        await _preKeyStore.StoreOneTimePreKeysPrivateAsync(_bobId, DeviceId.Primary, [(42, bobOpkPriv)]);

        var chatHandler = new FakeAppPayloadHandler(AppId.Chat);
        _appRouter.RegisterHandler(chatHandler);

        // Alice initiates X3DH
        var x3dhInitResult = X3dhAgreement.Initiate(_alicePrivKey, _aliceIdentityKey, bobBundle, _cryptoEngine).Value!;
        var aliceSession = DirectRatchetSession.CreateFromX3dhInitiator(
            _aliceId,
            _aliceDeviceId,
            _bobId,
            _bobDeviceId,
            x3dhInitResult,
            bobBundle.SignedPreKey,
            _cryptoEngine).Value!;

        var (counter, messageKey, _) = aliceSession.StepSendingChain(_cryptoEngine).Value;
        byte[] payloadData = "Greeting while stranger"u8.ToArray();
        byte[] inner = new byte[1 + payloadData.Length];
        inner[0] = AppId.Chat.Value;
        Buffer.BlockCopy(payloadData, 0, inner, 1, payloadData.Length);

        byte[] nonce = new byte[12];
        byte[] initialCiphertext = _cryptoEngine.EncryptAesGcm(messageKey.Span, nonce, inner, ReadOnlySpan<byte>.Empty);
        messageKey.Dispose();

        var envelope = new InboundHandshakeEnvelope(
            RecipientIdentityId: _bobId,
            SenderIdentityId: _aliceId,
            SenderDeviceId: _aliceDeviceId,
            SenderIdentityKey: _aliceIdentityKey,
            SenderEphemeralKey: x3dhInitResult.EphemeralPublicKey,
            SignedPreKeyId: 1,
            OneTimePreKeyId: 42,
            EncryptedPayload: initialCiphertext,
            ReceivedAtUtc: DateTimeOffset.UtcNow);

        // Act 1: Alice is stranger -> ReceiveInvitationAsync defers to PendingApproval
        var receiveResult = await _handshakeService.ReceiveInvitationAsync(envelope);
        receiveResult.IsSuccess.Should().BeTrue();

        // Contact is pending
        var contact = await _contactRepo.GetByPeerIdAsync(_bobId, _aliceId);
        contact.Should().NotBeNull();
        contact!.State.Should().Be(ContactState.PendingApproval);

        // Envelope is persisted in pending repository
        var pendingEnvelope = await _pendingHandshakeRepo.GetPendingHandshakeAsync(_bobId, _aliceId);
        pendingEnvelope.Should().NotBeNull();
        pendingEnvelope!.EncryptedPayload.ToArray().Should().BeEquivalentTo(initialCiphertext);

        // No session created yet, handler not invoked yet
        var sessionBefore = await _sessionRepo.GetSessionAsync(_bobId, _aliceId, _aliceDeviceId);
        sessionBefore.Should().BeNull();
        chatHandler.HandledContexts.Should().BeEmpty();

        // Act 2: Bob approves the contact and completes the handshake
        var approveResult = await _coordinator.ApproveRequestAsync(_bobId, _aliceId);
        approveResult.IsSuccess.Should().BeTrue();

        var completeResult = await _handshakeService.CompletePendingHandshakeAsync(_bobId, _aliceId);

        // Assert 2
        completeResult.IsSuccess.Should().BeTrue();

        // Session is now created and saved
        var sessionAfter = await _sessionRepo.GetSessionAsync(_bobId, _aliceId, _aliceDeviceId);
        sessionAfter.Should().NotBeNull();

        // App payload was decrypted and dispatched!
        chatHandler.HandledContexts.Should().HaveCount(1);
        chatHandler.CopiedPayloads[0].Should().BeEquivalentTo(payloadData);

        // Pending envelope is purged
        var pendingAfter = await _pendingHandshakeRepo.GetPendingHandshakeAsync(_bobId, _aliceId);
        pendingAfter.Should().BeNull();
    }
}
