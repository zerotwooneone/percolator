using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Moq;
using Percolator.Application.Identity;
using Percolator.Application.Sessions;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Sessions;
using SessionConversationId = Percolator.Sessions.ConversationId;
using SessionPeerId = Percolator.Sessions.PeerId;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;

namespace Percolator.ApplicationTests.Sessions;

[TestFixture]
public class SessionMessageTests
{
    private DirectSessionManager _aliceManager = null!;
    private DirectSessionManager _bobManager = null!;
    private Mock<IDoubleRatchetSessionStore> _aliceSessionStore = null!;
    private Mock<IDoubleRatchetSessionStore> _bobSessionStore = null!;
    private Mock<IConversationRepository> _mockConversationRepo = null!;
    private Mock<IMessageStore> _mockMessageStore = null!;

    private ActiveIdentityContext _aliceIdentity = null!;
    private ActiveIdentityContext _bobIdentity = null!;
    private Signature _bobSignature = null!;

    [SetUp]
    public void SetUp()
    {
        var aliceBackingStore = new Dictionary<(SessionPeerId, SessionConversationId), DoubleRatchetSession.DoubleRatchetSessionState>();
        _aliceSessionStore = new Mock<IDoubleRatchetSessionStore>();
        _aliceSessionStore.Setup(s => s.GetSessionStateAsync(It.IsAny<SessionPeerId>(), It.IsAny<SessionConversationId>()))
            .ReturnsAsync((SessionPeerId p, SessionConversationId c) => aliceBackingStore.TryGetValue((p, c), out var state) ? state : null);
        _aliceSessionStore.Setup(s => s.SaveSessionStateAsync(It.IsAny<SessionPeerId>(), It.IsAny<SessionConversationId>(), It.IsAny<DoubleRatchetSession.DoubleRatchetSessionState>()))
            .Callback((SessionPeerId p, SessionConversationId c, DoubleRatchetSession.DoubleRatchetSessionState s) => aliceBackingStore[(p, c)] = s);

        var bobBackingStore = new Dictionary<(SessionPeerId, SessionConversationId), DoubleRatchetSession.DoubleRatchetSessionState>();
        _bobSessionStore = new Mock<IDoubleRatchetSessionStore>();
        _bobSessionStore.Setup(s => s.GetSessionStateAsync(It.IsAny<SessionPeerId>(), It.IsAny<SessionConversationId>()))
            .ReturnsAsync((SessionPeerId p, SessionConversationId c) => bobBackingStore.TryGetValue((p, c), out var state) ? state : null);
        _bobSessionStore.Setup(s => s.SaveSessionStateAsync(It.IsAny<SessionPeerId>(), It.IsAny<SessionConversationId>(), It.IsAny<DoubleRatchetSession.DoubleRatchetSessionState>()))
            .Callback((SessionPeerId p, SessionConversationId c, DoubleRatchetSession.DoubleRatchetSessionState s) => bobBackingStore[(p, c)] = s);

        _mockConversationRepo = new Mock<IConversationRepository>();
        _mockMessageStore = new Mock<IMessageStore>();

        (_aliceIdentity, var aliceSignature) = CreateIdentityContext("Alice");
        (_bobIdentity, _bobSignature) = CreateIdentityContext("Bob");

        _aliceManager = new DirectSessionManager(
            _aliceSessionStore.Object,
            _mockConversationRepo.Object,
            _mockMessageStore.Object,
            _aliceIdentity);

        _bobManager = new DirectSessionManager(
            _bobSessionStore.Object,
            _mockConversationRepo.Object,
            _mockMessageStore.Object,
            _bobIdentity);
    }

    [Test]
    public async Task EncryptAndDecrypt_Should_SucceedSymmetrically_WhenSessionsAreEstablished()
    {
        // Arrange: Manually perform X3DH to get a shared secret
        var (aliceSharedSecret, bobSharedSecret) = PerformX3DH();
        var conversationId = new SessionConversationId(Guid.NewGuid());

        // Arrange: Establish sessions for both Alice and Bob
        var bobPeerId = new SessionPeerId(_bobIdentity.Identity!.Id);
        var bobIdentityKey = new SessionIdentityKey(_bobIdentity.Keys!.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo());
        var bobRatchetKey = new SessionRatchetKey(_bobIdentity.Keys!.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo());
        await _aliceManager.EstablishSessionAsInitiatorAsync(conversationId, bobPeerId, bobIdentityKey, bobRatchetKey, aliceSharedSecret);

        var alicePeerId = new SessionPeerId(_aliceIdentity.Identity!.Id);
        var aliceIdentityKey = new SessionIdentityKey(_aliceIdentity.Keys!.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo());
        await _bobManager.EstablishSessionAsResponderAsync(conversationId, alicePeerId, aliceIdentityKey, bobSharedSecret);

        // Arrange: Mock the conversation repository to resolve peer IDs
        var participants = new List<ParticipantId> { new(alicePeerId.Value), new(bobPeerId.Value) };
        var chatConversation = new Conversation(new ChatConversationId(conversationId.Value),new ChannelId(new byte[64]), participants,new List<Message>(), "Test Convo");
        _mockConversationRepo.Setup(r => r.GetByIdAsync(It.IsAny<ChatConversationId>())).ReturnsAsync(chatConversation);

        // Act: Alice encrypts a message
        var originalMessage = "This is a super secret message.";
        var originalBytes = Encoding.UTF8.GetBytes(originalMessage);
        var encryptedResult = await _aliceManager.EncryptMessageAsync(conversationId, originalBytes);

        // Act: Bob decrypts the message
        var decryptedBytes = await _bobManager.ReceiveMessageAsync(conversationId, encryptedResult!.Value.EncryptedMessage);

        // Assert: The decrypted message matches the original
        decryptedBytes.Should().BeEquivalentTo(originalBytes);
        Encoding.UTF8.GetString(decryptedBytes).Should().Be(originalMessage);
    }

    private (SharedSecret, SharedSecret) PerformX3DH()
    {
        var x3dhManager = new X3DHManager();

        // Alice (initiator) keys
        var aliceIdentityKey = _aliceIdentity.Keys!.IdentityAgreementKey;
        var aliceEphemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // Bob (responder) keys
        var bobIdentitySigningKey = _bobIdentity.Keys!.IdentitySigningKey;
        var bobIdentityAgreementKey = _bobIdentity.Keys!.IdentityAgreementKey;
        var bobSignedPreKey = _bobIdentity.Keys!.SignedPreKey;
        var bobOneTimePreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // Bob creates a signature for his signed pre-key
        var bobSignature = x3dhManager.SignPreKey(bobIdentitySigningKey, new PreKey(bobSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo()));

        // Alice receives Bob's pre-key bundle
        var bobPreKeyBundle = new PreKeyBundle(
            bobIdentitySigningKey.ExportSubjectPublicKeyInfo(),
            bobIdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo(),
            bobSignature,
            bobSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo(),
            bobOneTimePreKey.PublicKey.ExportSubjectPublicKeyInfo()
        );

        // Alice initiates the handshake to calculate her shared secret
        var aliceSharedSecret = x3dhManager.InitiateHandshake(bobPreKeyBundle, aliceEphemeralKey, aliceIdentityKey);

        // Bob receives Alice's initial message info and calculates his shared secret
        var bobSharedSecret = x3dhManager.RespondToHandshake(
            new RatchetIdentityKey(aliceIdentityKey.PublicKey.ExportSubjectPublicKeyInfo()),
            new RatchetEphemeralKey(aliceEphemeralKey.PublicKey.ExportSubjectPublicKeyInfo()),
            new PrivateAgreementKey(bobIdentityAgreementKey.ExportECPrivateKey()),
            new PrivatePreKey(bobSignedPreKey.ExportECPrivateKey()),
            new PrivateOneTimeKey(bobOneTimePreKey.ExportECPrivateKey())
        );

        aliceEphemeralKey.Dispose();
        bobOneTimePreKey.Dispose();

        return (aliceSharedSecret, bobSharedSecret);
    }

    private (ActiveIdentityContext, Signature) CreateIdentityContext(string name)
    {
        var signedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var identity = new IdentityRecord(Guid.NewGuid(), name);
        var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var agreementKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var signature = new X3DHManager().SignPreKey(signingKey, new PreKey(signedPreKey.PublicKey.ExportSubjectPublicKeyInfo()));

        var activeIdentity = new ActiveIdentityContext
        {
            Identity = identity,
            Keys = new X3dhKeys(
                signingKey,
                agreementKey,
                signedPreKey
            )
        };
        return (activeIdentity, signature);
    }
}