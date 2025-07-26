using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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
using CryptoSharedSecret = Percolator.Cryptography.SharedSecret;
using CryptoRatchetIdentityKey = Percolator.Cryptography.RatchetIdentityKey;
using CryptoRatchetEphemeralKey = Percolator.Cryptography.RatchetEphemeralKey;
using CryptoPrivateAgreementKey = Percolator.Cryptography.PrivateAgreementKey;
using CryptoPrivatePreKey = Percolator.Cryptography.PrivatePreKey;
using CryptoPrivateOneTimeKey = Percolator.Cryptography.PrivateOneTimeKey;
using CryptoPreKey = Percolator.Cryptography.PreKey;
using CryptoPreKeyBundle = Percolator.Cryptography.PreKeyBundle;
using Percolator.Cryptography.Primitives;

namespace Percolator.ApplicationTests.Sessions;

[TestFixture]
public class SessionMessageTests
{
    private IDoubleRatchetSessionStore _aliceSessionStore = null!;
    private IDoubleRatchetSessionStore _bobSessionStore = null!;
    private Mock<IConversationRepository> _mockConversationRepo = null!;
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

        // Create identity contexts
        _aliceIdentity = new ActiveIdentityContext
        {
            Identity = new IdentityRecord(Guid.NewGuid(), "Alice"),
            Keys = new X3dhKeys(
                ECDsa.Create(ECCurve.NamedCurves.nistP256),
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)
            )
        };

        _bobIdentity = new ActiveIdentityContext
        {
            Identity = new IdentityRecord(Guid.NewGuid(), "Bob"),
            Keys = new X3dhKeys(
                ECDsa.Create(ECCurve.NamedCurves.nistP256),
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)
            )
        };
        
        _aliceManager = new DirectSessionManager(
            _aliceSessionStore,
            _mockConversationRepo.Object,
            _aliceIdentity,
            new NullLogger<DirectSessionManager>());
        
        _bobManager = new DirectSessionManager(
            _bobSessionStore, 
            _mockConversationRepo.Object,
            _bobIdentity,
            new NullLogger<DirectSessionManager>());
    }

    [Test]
    public async Task EncryptAndDecrypt_Should_SucceedSymmetrically_WhenSessionsAreEstablished()
    {
        // Arrange: Establish a shared secret between Alice and Bob
        var (aliceSharedSecret, bobSharedSecret) = PerformX3DH();

        // Arrange: Use the shared secret to establish a double ratchet session
        var conversationId = new SessionConversationId(Guid.NewGuid());
        var bobPeerId = new SessionPeerId(_bobIdentity.Identity!.Id);
        var bobIdentityKey = new CryptoRatchetIdentityKey(_bobIdentity.Keys!.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo());
        var bobRatchetKey = new CryptoRatchetEphemeralKey(_bobIdentity.Keys!.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo());
        await _aliceManager.EstablishSessionAsInitiatorAsync(conversationId, bobPeerId, bobIdentityKey, bobRatchetKey, new CryptoSharedSecret(aliceSharedSecret.Value));

        var alicePeerId = new SessionPeerId(_aliceIdentity.Identity!.Id);
        var aliceIdentityKey = new CryptoRatchetIdentityKey(_aliceIdentity.Keys!.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo());
        await _bobManager.EstablishSessionAsResponderAsync(conversationId, alicePeerId, aliceIdentityKey, new CryptoSharedSecret(bobSharedSecret.Value));

        // Arrange: Mock the conversation repository to allow the manager to resolve the remote peer ID.
        var participants = new List<ParticipantId> { new(alicePeerId.Value), new(bobPeerId.Value) };
        var chatConversation = new Conversation(new ChatConversationId(conversationId.Value), new ChannelId(new byte[64]), participants, new List<Message>(), "Test Convo");
        _mockConversationRepo.Setup(r => r.GetByIdAsync(It.IsAny<ChatConversationId>()))
            .ReturnsAsync(chatConversation);

        // Arrange: Mock the protocol to correctly link the output of the encryption mock with the input of the decryption mock
        var originalMessage = "This is a super secret message.";
        var originalBytes = Encoding.UTF8.GetBytes(originalMessage);
        var encryptedBytes = new byte[] { 1, 2, 3, 4, 5 }; // Dummy encrypted data

        // Act: Alice encrypts a message
        var encryptedResult = await _aliceManager.EncryptMessageAsync(conversationId, new Plaintext(originalBytes));

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
}