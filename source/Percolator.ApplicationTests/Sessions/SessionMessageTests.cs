using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
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
    private Mock<IPeerRepository> _mockPeerRepo = null!;
    private Mock<IMessageStore> _mockMessageStore = null!;

    private ActiveIdentityContext _aliceIdentity = null!;
    private Signature _aliceSignature = null!;
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
        _mockPeerRepo = new Mock<IPeerRepository>();
        _mockMessageStore = new Mock<IMessageStore>();

        (_aliceIdentity, _aliceSignature) = CreateIdentityContext("Alice");
        (_bobIdentity, _bobSignature) = CreateIdentityContext("Bob");

        var mockAliceLocalPeerProvider = new Mock<ILocalPeerProvider>();
        mockAliceLocalPeerProvider.Setup(p => p.GetPeerIdAsync()).ReturnsAsync(new SessionPeerId(_aliceIdentity.Identity!.Id));

        var mockBobLocalPeerProvider = new Mock<ILocalPeerProvider>();
        mockBobLocalPeerProvider.Setup(p => p.GetPeerIdAsync()).ReturnsAsync(new SessionPeerId(_bobIdentity.Identity!.Id));

        _aliceManager = new DirectSessionManager(
            _aliceSessionStore.Object,
            _mockConversationRepo.Object,
            _mockPeerRepo.Object,
            mockAliceLocalPeerProvider.Object,
            _mockMessageStore.Object,
            _aliceIdentity);

        _bobManager = new DirectSessionManager(
            _bobSessionStore.Object,
            _mockConversationRepo.Object,
            _mockPeerRepo.Object,
            mockBobLocalPeerProvider.Object,
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
        var bobIdentityKey = new OpaquePublicKey(_bobIdentity.Keys!.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo());
        var bobRatchetKey = new OpaquePublicKey(_bobIdentity.Keys!.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo());
        await _aliceManager.EstablishSessionAsInitiatorAsync(conversationId, bobPeerId, bobIdentityKey, bobRatchetKey, aliceSharedSecret);

        var alicePeerId = new SessionPeerId(_aliceIdentity.Identity!.Id);
        var aliceIdentityKey = new OpaquePublicKey(_aliceIdentity.Keys!.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo());
        await _bobManager.EstablishSessionAsResponderAsync(conversationId, alicePeerId, aliceIdentityKey, bobSharedSecret);

        // Arrange: Mock the conversation repository to resolve peer IDs
        var participants = new List<ParticipantId> { new(alicePeerId.Value), new(bobPeerId.Value) };
        var chatConversation = new Conversation(new ChatConversationId(conversationId.Value), participants, "Test Convo");
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
        var aliceEphemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var bobPreKeyBundle = new PreKeyBundle(
            _bobIdentity.Keys!.IdentitySigningKey.ExportSubjectPublicKeyInfo(),
            _bobIdentity.Keys!.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo(),
            _bobSignature,
            _bobIdentity.Keys!.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo(),
            _bobIdentity.Keys!.OneTimePreKeys[0].PublicKey.ExportSubjectPublicKeyInfo()
        );

        var aliceSharedSecret = x3dhManager.InitiateHandshake(bobPreKeyBundle, aliceEphemeralKey, _aliceIdentity.Keys!.IdentityAgreementKey);

        var bobSharedSecret = x3dhManager.RespondToHandshake(
            new PublicKey(_aliceIdentity.Keys!.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
            new PublicKey(aliceEphemeralKey.PublicKey.ExportSubjectPublicKeyInfo()),
            new PrivateKey(_bobIdentity.Keys!.IdentityAgreementKey.ExportECPrivateKey()),
            new PrivateKey(_bobIdentity.Keys!.SignedPreKey.ExportECPrivateKey()),
            new PrivateKey(_bobIdentity.Keys!.OneTimePreKeys[0].ExportECPrivateKey())
        );

        return (aliceSharedSecret, bobSharedSecret);
    }

    private (ActiveIdentityContext, Signature) CreateIdentityContext(string name)
    {
        var signedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var identity = new IdentityRecord(Guid.NewGuid(), name);
        var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var agreementKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var signature = new X3DHManager().SignPreKey(signingKey, new PublicKey(signedPreKey.PublicKey.ExportSubjectPublicKeyInfo()));

        var activeIdentity = new ActiveIdentityContext
        {
            Identity = identity,
            Keys = new X3dhKeys(
                signingKey,
                agreementKey,
                signedPreKey,
                new[] { ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256) }
            )
        };
        return (activeIdentity, signature);
    }
}