using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.Sessions;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Identity;
using Percolator.Identity.Model;
using Message = Percolator.Chat.Message;
using ParticipantId = Percolator.Chat.ValueObjects.ParticipantId;

namespace Percolator.ApplicationTests.Sessions;

[TestFixture]
public class DirectSessionManagerTests
{
    // Alice (initiator) components
    private Mock<IDoubleRatchetSessionStore> _aliceSessionStore = null!;
    private Mock<IConversationRepository> _aliceConversationRepository = null!;
    private Mock<IPeerRepository> _alicePeerRepository = null!;
    private ActiveIdentityContext _aliceIdentityContext = null!;
    private DirectSessionManager _aliceSessionManager = null!;
    private X3dhKeys _aliceKeys = null!;
    private Identity.PeerId _alicePeerId = null!;
    
    // Bob (responder) components
    private Mock<IDoubleRatchetSessionStore> _bobSessionStore = null!;
    private Mock<IConversationRepository> _bobConversationRepository = null!;
    private Mock<IPeerRepository> _bobPeerRepository = null!;
    private ActiveIdentityContext _bobIdentityContext = null!;
    private DirectSessionManager _bobSessionManager = null!;
    private X3dhKeys _bobKeys = null!;
    private Identity.PeerId _bobPeerId = null!;
    
    // Shared components
    private SessionId _sessionId = null!;
    private ILoggerFactory _loggerFactory = null!;
    private ECDiffieHellman _aliceEphemeral;
    private DoubleRatchetSession.DoubleRatchetSessionState? _aliceSessionState;
    private DoubleRatchetSession.DoubleRatchetSessionState? _bobSessionState;

    [SetUp]
    public void Setup()
    {
        _loggerFactory = new NullLoggerFactory();
        
        // Create unique session ID
        _sessionId = new SessionId(Guid.NewGuid());
        
        // Generate Alice's identity and keys
        _alicePeerId = new Identity.PeerId(Guid.NewGuid());
        _aliceEphemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _aliceKeys = new X3dhKeys(
            ECDsa.Create(ECCurve.NamedCurves.nistP256),
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
            _aliceEphemeral);
            
        _aliceSessionStore = new Mock<IDoubleRatchetSessionStore>();
        _aliceConversationRepository = new Mock<IConversationRepository>();
        _alicePeerRepository = new Mock<IPeerRepository>();
        
        // Create a real ActiveIdentityContext instance instead of a mock
        _aliceIdentityContext = new ActiveIdentityContext();
        // Set properties directly
        _aliceIdentityContext.Identity = new IdentityRecord(_alicePeerId.Value, "Alice");
        _aliceIdentityContext.Keys = _aliceKeys;
        
        _aliceSessionManager = new DirectSessionManager(
            _aliceSessionStore.Object,
            _aliceConversationRepository.Object,
            _aliceIdentityContext,
            _loggerFactory.CreateLogger<DirectSessionManager>(),
            _loggerFactory);
            
        // Generate Bob's identity and keys
        _bobPeerId = new Identity.PeerId(Guid.NewGuid());
        _bobKeys = new X3dhKeys(
            ECDsa.Create(ECCurve.NamedCurves.nistP256),
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));
            
        _bobSessionStore = new Mock<IDoubleRatchetSessionStore>();
        _bobConversationRepository = new Mock<IConversationRepository>();
        _bobPeerRepository = new Mock<IPeerRepository>();
        
        // Create a real ActiveIdentityContext instance instead of a mock
        _bobIdentityContext = new ActiveIdentityContext();
        // Set properties directly
        _bobIdentityContext.Identity = new IdentityRecord(_bobPeerId.Value, "Bob");
        _bobIdentityContext.Keys = _bobKeys;
        
        _bobSessionManager = new DirectSessionManager(
            _bobSessionStore.Object,
            _bobConversationRepository.Object,
            _bobIdentityContext,
            _loggerFactory.CreateLogger<DirectSessionManager>(),
            _loggerFactory);
            
        // Setup session store mocks to use class-level state variables
        _aliceSessionStore.Setup(x => x.SetSessionStateAsync(It.IsAny<SessionId>(), It.IsAny<DoubleRatchetSession.DoubleRatchetSessionState>()))
            .Callback<SessionId, DoubleRatchetSession.DoubleRatchetSessionState>((sessionId, state) => 
            {
                if (sessionId == _sessionId)
                {
                    _aliceSessionState = state;
                }
            })
            .Returns(Task.CompletedTask);
        _bobSessionStore.Setup(x => x.SetSessionStateAsync(It.IsAny<SessionId>(), It.IsAny<DoubleRatchetSession.DoubleRatchetSessionState>()))
            .Callback<SessionId, DoubleRatchetSession.DoubleRatchetSessionState>((sessionId, state) => 
            {
                if (sessionId == _sessionId)
                {
                    _bobSessionState = state;
                }
            })
            .Returns(Task.CompletedTask);
            
        _aliceSessionStore.Setup(x => x.GetSessionStateAsync(It.IsAny<SessionId>()))
            .ReturnsAsync(() => _aliceSessionState);
        _bobSessionStore.Setup(x => x.GetSessionStateAsync(It.IsAny<SessionId>()))
            .ReturnsAsync(() => _bobSessionState);
    }

    [TearDown]
    public void TearDown()
    {
        // Dispose the cryptographic keys
        _aliceKeys?.Dispose();
        _bobKeys?.Dispose();
        _aliceEphemeral?.Dispose();
        
        // Dispose the logger factory
        (_loggerFactory as IDisposable)?.Dispose();
    }

    [Test]
    public async Task SessionSymmetry_WithBidirectionalMessages_MaintainsConsistentState()
    {
        // Arrange
        await EstablishSessionsAsync();
        
        // Record initial states
        var aliceInitialRootKey = _aliceSessionState!.RootKey;
        var bobInitialRootKey = _bobSessionState!.RootKey;
        
        // Act: Alice encrypts a message for Bob
        var aliceMessage = new Plaintext(System.Text.Encoding.UTF8.GetBytes("Hello from Alice!"));
        var aliceEncrypted = await _aliceSessionManager.EncryptMessageAsync(_sessionId, aliceMessage);
        
        // Act: Bob receives Alice's message
        var bobDecrypted = await _bobSessionManager.ReceiveMessageAsync(_sessionId, aliceEncrypted.Value.encryptedMessage);
        
        // Assert
        Assert.That(bobDecrypted, Is.Not.Null, "Message should be decrypted successfully");
        Assert.That(System.Text.Encoding.UTF8.GetString(bobDecrypted.Value), Is.EqualTo("Hello from Alice!"), 
            "Decrypted message should match original plaintext");
            
        // Act: Bob sends a response to Alice
        var bobMessage = new Plaintext(System.Text.Encoding.UTF8.GetBytes("Hello from Bob!"));
        var bobEncrypted = await _bobSessionManager.EncryptMessageAsync(_sessionId, bobMessage);
        
        // Act: Alice receives Bob's message
        var aliceDecrypted = await _aliceSessionManager.ReceiveMessageAsync(_sessionId, bobEncrypted.Value.encryptedMessage);
        
        // Assert
        Assert.That(aliceDecrypted, Is.Not.Null, "Message should be decrypted successfully");
        Assert.That(System.Text.Encoding.UTF8.GetString(aliceDecrypted.Value), Is.EqualTo("Hello from Bob!"), 
            "Decrypted message should match original plaintext");
            
        Assert.That(_aliceSessionState!.RootKey, Is.Not.EqualTo(aliceInitialRootKey), 
            "Alice's root key should have changed after ratchet");
        Assert.That(_bobSessionState!.RootKey, Is.Not.EqualTo(bobInitialRootKey), 
            "Bob's root key should have changed after ratchet");
        
        Assert.That(_aliceSessionState!.PreviousChainLength, Is.EqualTo(1), 
            "Alice should have updated previous chain length after ratchet");
        Assert.That(_bobSessionState!.PreviousChainLength, Is.EqualTo(0),
            "Bob's previous chain length should be 0 (he hadn't sent any messages before receiving)");
    }
    
    [Test]
    public async Task SessionSymmetry_ResponderInitiatesMessage_MaintainsConsistentState()
    {
        // Arrange
        await EstablishSessionsAsync();

        // Act & Assert: Bob (responder) sends the first message to Alice
        var bobMessage = new Plaintext(System.Text.Encoding.UTF8.GetBytes("Hello from Bob!"));
        var bobEncrypted = await _bobSessionManager.EncryptMessageAsync(_sessionId, bobMessage);
        var aliceDecrypted = await _aliceSessionManager.ReceiveMessageAsync(_sessionId, bobEncrypted.Value.encryptedMessage);

        Assert.That(aliceDecrypted, Is.Not.Null, "Alice should decrypt Bob's message successfully");
        Assert.That(System.Text.Encoding.UTF8.GetString(aliceDecrypted.Value), Is.EqualTo("Hello from Bob!"), "Decrypted message should match original");

        // Act & Assert: Alice (initiator) sends a reply to Bob
        var aliceMessage = new Plaintext(System.Text.Encoding.UTF8.GetBytes("Hello from Alice!"));
        var aliceEncrypted = await _aliceSessionManager.EncryptMessageAsync(_sessionId, aliceMessage);
        var bobDecrypted = await _bobSessionManager.ReceiveMessageAsync(_sessionId, aliceEncrypted.Value.encryptedMessage);

        Assert.That(bobDecrypted, Is.Not.Null, "Bob should decrypt Alice's message successfully");
        Assert.That(System.Text.Encoding.UTF8.GetString(bobDecrypted.Value), Is.EqualTo("Hello from Alice!"), "Decrypted reply should match original");

        // Assert: Final state verification
        Assert.That(_aliceSessionState!.RootKey, Is.EqualTo(_bobSessionState!.RootKey), "Root keys should be the same after the initial ratchet");
        Assert.That(_aliceSessionState.SendingCounter, Is.EqualTo(1), "Alice should have a sending counter of 1");
        Assert.That(_bobSessionState.SendingCounter, Is.EqualTo(1), "Bob should have a sending counter of 1");
    }

    [Test]
    public async Task PreviousChainLength_AfterMultipleRatchets_IsCorrectlyTracked()
    {
        // Arrange
        await EstablishSessionsAsync();
        
        // Assert: Initial state verification
        Assert.That(_aliceSessionState.PreviousChainLength, Is.EqualTo(0), "Alice's initial previous chain length should be 0");
        Assert.That(_bobSessionState.PreviousChainLength, Is.EqualTo(0), "Bob's initial previous chain length should be 0");
            
        // Act & Assert: Multiple rounds of message exchanges
        for (int i = 1; i <= 3; i++)
        {
            var aliceMessage = new Plaintext(System.Text.Encoding.UTF8.GetBytes($"Alice message {i}"));
            var aliceEncrypted = await _aliceSessionManager.EncryptMessageAsync(_sessionId, aliceMessage);
            await _bobSessionManager.ReceiveMessageAsync(_sessionId, aliceEncrypted.Value.encryptedMessage);

            var bobMessage = new Plaintext(System.Text.Encoding.UTF8.GetBytes($"Bob message {i}"));
            var bobEncrypted = await _bobSessionManager.EncryptMessageAsync(_sessionId, bobMessage);
            await _aliceSessionManager.ReceiveMessageAsync(_sessionId, bobEncrypted.Value.encryptedMessage);
        }
        
        // Assert: Final verification after multiple ratchets
        Assert.That(_aliceSessionState.PreviousChainLength, Is.EqualTo(1), "Alice's final previous chain length should be 1");
        Assert.That(_bobSessionState.PreviousChainLength, Is.EqualTo(1), "Bob's final previous chain length should be 1");
    }
    
    [Test]
    public async Task OutOfOrderMessages_AreHandledCorrectly()
    {
        // Arrange
        await EstablishSessionsAsync();
        
        // Act: Alice sends three messages to Bob
        var aliceMsg1 = new Plaintext(System.Text.Encoding.UTF8.GetBytes("Message 1"));
        var encryptedMsg1 = await _aliceSessionManager.EncryptMessageAsync(_sessionId, aliceMsg1);

        var aliceMsg2 = new Plaintext(System.Text.Encoding.UTF8.GetBytes("Message 2"));
        var encryptedMsg2 = await _aliceSessionManager.EncryptMessageAsync(_sessionId, aliceMsg2);

        var aliceMsg3 = new Plaintext(System.Text.Encoding.UTF8.GetBytes("Message 3"));
        var encryptedMsg3 = await _aliceSessionManager.EncryptMessageAsync(_sessionId, aliceMsg3);

        // Act: Bob receives them out of order (3, then 1, then 2)
        var decryptedMsg3 = await _bobSessionManager.ReceiveMessageAsync(_sessionId, encryptedMsg3.Value.encryptedMessage);
        var decryptedMsg1 = await _bobSessionManager.ReceiveMessageAsync(_sessionId, encryptedMsg1.Value.encryptedMessage);
        var decryptedMsg2 = await _bobSessionManager.ReceiveMessageAsync(_sessionId, encryptedMsg2.Value.encryptedMessage);

        // Assert
        Assert.That(System.Text.Encoding.UTF8.GetString(decryptedMsg1.Value), Is.EqualTo("Message 1"));
        Assert.That(System.Text.Encoding.UTF8.GetString(decryptedMsg2.Value), Is.EqualTo("Message 2"));
        Assert.That(System.Text.Encoding.UTF8.GetString(decryptedMsg3.Value), Is.EqualTo("Message 3"));
    }
    
    private async Task EstablishSessionsAsync()
    {
        var sharedSecret = new SharedSecret(new byte[32]);
        var bobIdentityKeyPublic = new RatchetIdentityKey(_bobKeys.IdentityAgreementKey.ExportSubjectPublicKeyInfo());
        var bobPreKeyPublic = new RatchetEphemeralKey(_bobKeys.SignedPreKey.ExportSubjectPublicKeyInfo());
        var aliceIdentityKeyPublic = new RatchetIdentityKey(_aliceKeys.IdentityAgreementKey.ExportSubjectPublicKeyInfo());
        var aliceEphemeralKeyPublic = new RatchetEphemeralKey(_aliceKeys.SignedPreKey.ExportSubjectPublicKeyInfo());

        var aliceConversation = new Conversation(
            new ConversationId(_sessionId.Value),
            new ChannelId(Guid.NewGuid().ToByteArray()),
            new List<ParticipantId> { new(_alicePeerId.Value), new(_bobPeerId.Value) },
            new List<Message>());
        var bobConversation = new Conversation(
            new ConversationId(_sessionId.Value),
            new ChannelId(Guid.NewGuid().ToByteArray()),
            new List<ParticipantId> { new(_alicePeerId.Value), new(_bobPeerId.Value) },
            new List<Message>());
        _aliceConversationRepository.Setup(x => x.GetByIdAsync(It.IsAny<ConversationId>())).ReturnsAsync(aliceConversation);
        _bobConversationRepository.Setup(x => x.GetByIdAsync(It.IsAny<ConversationId>())).ReturnsAsync(bobConversation);

        await _aliceSessionManager.EstablishSessionAsInitiatorAsync(_sessionId, _bobPeerId, bobIdentityKeyPublic, bobPreKeyPublic, sharedSecret, _aliceEphemeral);
        await _bobSessionManager.EstablishSessionAsResponderAsync(_sessionId, _alicePeerId, aliceIdentityKeyPublic, aliceEphemeralKeyPublic, _bobKeys.SignedPreKey, sharedSecret);

        Assert.That(_aliceSessionState, Is.Not.Null);
        Assert.That(_bobSessionState, Is.Not.Null);
    }
}
