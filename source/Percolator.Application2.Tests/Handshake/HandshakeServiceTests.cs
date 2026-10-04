using Percolator.Application2.Handshake;
using Percolator.Application2.Ingress;
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
        _outboxRepo = new InMemoryOutboxRepository();
        _cryptoEngine = new ApplicationTestCryptoEngine();
        _appRouter = new AppRouter();

        _handshakeService = new HandshakeService(
            _identityKeyStore,
            _preKeyStore,
            _contactRepo,
            _coordinator,
            _sessionRepo,
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
}
