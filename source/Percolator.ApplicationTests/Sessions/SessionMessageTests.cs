using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.DataCollection;
using Moq;
using Percolator.Application.Identity;
using Percolator.Application.Sessions;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Infrastructure.Identity;
using Percolator.Sessions;
using SessionConversationId = Percolator.Sessions.ConversationId;
using SessionRatchetIdentityKey = Percolator.Sessions.RatchetIdentityKey;
using SessionRatchetEphemeralKey = Percolator.Sessions.RatchetEphemeralKey;
using SessionPrivateEphemeralKey = Percolator.Sessions.PrivateEphemeralKey;
using SessionSharedSecret = Percolator.Sessions.SharedSecret;
using SessionPeerId = Percolator.Sessions.PeerId;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using CryptoSharedSecret = Percolator.Cryptography.SharedSecret;

using SessionState = Percolator.Sessions.SessionState;
using SessionPlaintext = Percolator.Sessions.Plaintext;
using SessionRatchetMessage = Percolator.Sessions.RatchetMessage;
using SessionIdentityKey = Percolator.Sessions.SessionIdentityKey;
using SessionRatchetKey = Percolator.Sessions.SessionRatchetKey;
using CryptoRatchetIdentityKey = Percolator.Cryptography.RatchetIdentityKey;
using CryptoRatchetEphemeralKey = Percolator.Cryptography.RatchetEphemeralKey;
using CryptoPrivateAgreementKey = Percolator.Cryptography.PrivateAgreementKey;
using CryptoPrivatePreKey = Percolator.Cryptography.PrivatePreKey;
using CryptoPrivateOneTimeKey = Percolator.Cryptography.PrivateOneTimeKey;
using CryptoPreKey = Percolator.Cryptography.PreKey;
using CryptoPreKeyBundle = Percolator.Cryptography.PreKeyBundle;

namespace Percolator.ApplicationTests.Sessions;

[TestFixture]
public class SessionMessageTests
{
    private IDoubleRatchetSessionStore _aliceSessionStore = null!;
    private IDoubleRatchetSessionStore _bobSessionStore = null!;
    private Mock<IConversationRepository> _mockConversationRepo = null!;
    private Mock<IMessageStore> _mockMessageStore = null!;
    private Mock<IDoubleRatchetProtocol> _mockProtocol = null!;
    private DirectSessionManager _aliceManager = null!;
    private DirectSessionManager _bobManager = null!;
    private ActiveIdentityContext _aliceIdentity = null!;
    private ActiveIdentityContext _bobIdentity = null!;

    [SetUp]
    public void SetUp()
    {
        _aliceSessionStore = new FakeDoubleRatchetSessionStore();
        _bobSessionStore = new FakeDoubleRatchetSessionStore();

        _mockConversationRepo = new Mock<IConversationRepository>();
        _mockMessageStore = new Mock<IMessageStore>();
        _mockProtocol = new Mock<IDoubleRatchetProtocol>();

        var dummySessionState = new SessionState(new byte[32]);
        _mockProtocol.Setup(p => p.InitiateSession(It.IsAny<SessionRatchetIdentityKey>(), It.IsAny<SessionRatchetEphemeralKey>(), It.IsAny<SessionSharedSecret>()))
            .Returns((dummySessionState, new SessionRatchetEphemeralKey(new byte[33])));
        _mockProtocol.Setup(p => p.RespondToSession(It.IsAny<SessionRatchetIdentityKey>(), It.IsAny<SessionPrivateEphemeralKey>(), It.IsAny<SessionSharedSecret>()))
            .Returns(dummySessionState);

        (_aliceIdentity, _) = CreateIdentityContext("Alice");
        (_bobIdentity, _) = CreateIdentityContext("Bob");

        _aliceManager = new DirectSessionManager(
            _aliceSessionStore,
            _mockConversationRepo.Object,
            _mockMessageStore.Object,
            _aliceIdentity,
            _mockProtocol.Object
        );

        _bobManager = new DirectSessionManager(
            _bobSessionStore,
            _mockConversationRepo.Object,
            _mockMessageStore.Object,
            _bobIdentity,
            _mockProtocol.Object
        );
    }

    [Test]
    public async Task EncryptAndDecrypt_Should_SucceedSymmetrically_WhenSessionsAreEstablished()
    {
        // Arrange: Establish a shared secret between Alice and Bob
        var (aliceSharedSecret, bobSharedSecret) = PerformX3DH();

        // Arrange: Use the shared secret to establish a double ratchet session
        var conversationId = new SessionConversationId(Guid.NewGuid());
        var bobPeerId = new SessionPeerId(_bobIdentity.Identity!.Id);
        var bobIdentityKey = new SessionIdentityKey(_bobIdentity.Keys!.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo());
        var bobRatchetKey = new SessionRatchetKey(_bobIdentity.Keys!.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo());
        await _aliceManager.EstablishSessionAsInitiatorAsync(conversationId, bobPeerId, bobIdentityKey, bobRatchetKey, new SessionSharedSecret(aliceSharedSecret.Value));

        var alicePeerId = new SessionPeerId(_aliceIdentity.Identity!.Id);
        var aliceIdentityKey = new SessionIdentityKey(_aliceIdentity.Keys!.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo());
        await _bobManager.EstablishSessionAsResponderAsync(conversationId, alicePeerId, aliceIdentityKey, new SessionSharedSecret(bobSharedSecret.Value));

        // Arrange: Mock the conversation repository to allow the manager to resolve the remote peer ID.
        var participants = new List<ParticipantId> { new(alicePeerId.Value), new(bobPeerId.Value) };
        var chatConversation = new Conversation(new ChatConversationId(conversationId.Value), new ChannelId(new byte[64]), participants, new List<Message>(), "Test Convo");
        _mockConversationRepo.Setup(r => r.GetByIdAsync(It.IsAny<ChatConversationId>()))
            .ReturnsAsync(chatConversation);

        // Arrange: Mock the protocol to correctly link the output of the encryption mock with the input of the decryption mock
        var originalMessage = "This is a super secret message.";
        var originalBytes = Encoding.UTF8.GetBytes(originalMessage);
        var encryptedBytes = new byte[] { 1, 2, 3, 4, 5 }; // Dummy encrypted data

        _mockProtocol.Setup(p => p.Encrypt(It.IsAny<SessionState>(), It.Is<SessionPlaintext>(pt => pt.Value.SequenceEqual(originalBytes))))
            .Returns((SessionState state, SessionPlaintext plaintext) =>
                (state, new SessionRatchetMessage(encryptedBytes)));

        _mockProtocol.Setup(p => p.Decrypt(It.IsAny<SessionState>(), It.Is<SessionRatchetMessage>(rm => rm.Value.SequenceEqual(encryptedBytes))))
            .Returns((SessionState state, SessionRatchetMessage message) =>
                (state, new SessionPlaintext(originalBytes)));

        // Act: Alice encrypts a message
        var encryptedResult = await _aliceManager.EncryptMessageAsync(conversationId, new SessionPlaintext(originalBytes));

        // Act: Bob decrypts the message
        var decryptedBytes = await _bobManager.ReceiveMessageAsync(conversationId, encryptedResult!.Value.encryptedMessage);

        // Assert: The decrypted message matches the original
        decryptedBytes.Should().NotBeNull();
        decryptedBytes!.Value.Should().BeEquivalentTo(originalBytes);
        Encoding.UTF8.GetString(decryptedBytes!.Value).Should().Be(originalMessage);
    }

    private (CryptoSharedSecret, CryptoSharedSecret) PerformX3DH()
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
        var bobSignature = x3dhManager.SignPreKey(bobIdentitySigningKey, new CryptoPreKey(bobSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo()));

        // Alice receives Bob's pre-key bundle
        var bobPreKeyBundle = new CryptoPreKeyBundle(
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
            new CryptoRatchetIdentityKey(aliceIdentityKey.PublicKey.ExportSubjectPublicKeyInfo()),
            new CryptoRatchetEphemeralKey(aliceEphemeralKey.PublicKey.ExportSubjectPublicKeyInfo()),
            new CryptoPrivateAgreementKey(bobIdentityAgreementKey.ExportECPrivateKey()),
            new CryptoPrivatePreKey(bobSignedPreKey.ExportECPrivateKey()),
            new CryptoPrivateOneTimeKey(bobOneTimePreKey.ExportECPrivateKey())
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
        var signature = new X3DHManager().SignPreKey(signingKey, new CryptoPreKey(signedPreKey.PublicKey.ExportSubjectPublicKeyInfo()));

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