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
    
    [SetUp]
    public void Setup()
    {
        _loggerFactory = new NullLoggerFactory();
        
        // Create unique session ID
        _sessionId = new SessionId(Guid.NewGuid());
        
        // Generate Alice's identity and keys
        _alicePeerId = new Identity.PeerId(Guid.NewGuid());
        _aliceKeys = new X3dhKeys(
            ECDsa.Create(ECCurve.NamedCurves.nistP256),
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));
            
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
    }

    [TearDown]
    public void TearDown()
    {
        // Dispose the cryptographic keys
        _aliceKeys?.Dispose();
        _bobKeys?.Dispose();
        
        // Dispose the logger factory
        (_loggerFactory as IDisposable)?.Dispose();
    }

    [Test]
    public async Task SessionSymmetry_WithBidirectionalMessages_MaintainsConsistentState()
    {
        // Setup shared secret and ratchet keys
        var sharedSecret = new SharedSecret(new byte[32]); // Use a zero-filled shared secret for testing
        
        // Extract public keys needed for session establishment
        var bobIdentityKeyPublic = new RatchetIdentityKey(_bobKeys.IdentityAgreementKey.ExportSubjectPublicKeyInfo());
        var bobPreKeyPublic = new RatchetEphemeralKey(_bobKeys.SignedPreKey.ExportSubjectPublicKeyInfo());
        var aliceIdentityKeyPublic = new RatchetIdentityKey(_aliceKeys.IdentityAgreementKey.ExportSubjectPublicKeyInfo());
        
        // Setup conversation mocks
        var conversation = new Conversation(
            new ConversationId(_sessionId.Value),
            new ChannelId(Guid.NewGuid().ToByteArray()),
            new List<ParticipantId> { new ParticipantId(_alicePeerId.Value), new ParticipantId(_bobPeerId.Value) },
            new List<Message>());
            
        _aliceConversationRepository.Setup(x => x.GetByIdAsync(It.IsAny<ConversationId>()))
            .ReturnsAsync(conversation);
            
        // Mirror the conversation for Bob's perspective
        var conversationForBob = new Conversation(
            new ConversationId(_sessionId.Value),
            new ChannelId(Guid.NewGuid().ToByteArray()),
            new List<ParticipantId> { new ParticipantId(_bobPeerId.Value), new ParticipantId(_alicePeerId.Value) },
            new List<Message>());
            
        _bobConversationRepository.Setup(x => x.GetByIdAsync(It.IsAny<ConversationId>()))
            .ReturnsAsync(conversationForBob);
            
        // Track session states
        DoubleRatchetSession.DoubleRatchetSessionState? aliceSessionState = null;
        DoubleRatchetSession.DoubleRatchetSessionState? bobSessionState = null;
        
        // Setup session store mocks
        _aliceSessionStore.Setup(x => x.SetSessionStateAsync(It.IsAny<SessionId>(), It.IsAny<DoubleRatchetSession.DoubleRatchetSessionState>()))
            .Callback<SessionId, DoubleRatchetSession.DoubleRatchetSessionState>((id, state) => aliceSessionState = state)
            .Returns(Task.CompletedTask);
            
        _bobSessionStore.Setup(x => x.SetSessionStateAsync(It.IsAny<SessionId>(), It.IsAny<DoubleRatchetSession.DoubleRatchetSessionState>()))
            .Callback<SessionId, DoubleRatchetSession.DoubleRatchetSessionState>((id, state) => bobSessionState = state)
            .Returns(Task.CompletedTask);
            
        _aliceSessionStore.Setup(x => x.GetSessionStateAsync(It.IsAny<SessionId>()))
            .ReturnsAsync(() => aliceSessionState);
            
        _bobSessionStore.Setup(x => x.GetSessionStateAsync(It.IsAny<SessionId>()))
            .ReturnsAsync(() => bobSessionState);
            
        // Establish the initial sessions
        await _aliceSessionManager.EstablishSessionAsInitiatorAsync(
            _sessionId, 
            _bobPeerId, 
            bobIdentityKeyPublic, 
            bobPreKeyPublic, 
            sharedSecret);
            
        await _bobSessionManager.EstablishSessionAsResponderAsync(
            _sessionId, 
            _alicePeerId, 
            aliceIdentityKeyPublic,
            _bobKeys.SignedPreKey, 
            sharedSecret);
            
        // Verify that sessions were established
        Assert.That(aliceSessionState, Is.Not.Null, "Alice's session state should not be null");
        Assert.That(bobSessionState, Is.Not.Null, "Bob's session state should not be null");
        
        // Record initial states
        var aliceInitialRootKey = aliceSessionState!.RootKey;
        var bobInitialRootKey = bobSessionState!.RootKey;
        
        // Alice encrypts a message for Bob
        var aliceMessage = new Plaintext(System.Text.Encoding.UTF8.GetBytes("Hello from Alice!"));
        var aliceEncrypted = await _aliceSessionManager.EncryptMessageAsync(_sessionId, aliceMessage);
        
        // Bob receives Alice's message
        var bobDecrypted = await _bobSessionManager.ReceiveMessageAsync(_sessionId, aliceEncrypted.Value.encryptedMessage);
        
        // Verify that the message was properly decrypted
        Assert.That(bobDecrypted, Is.Not.Null, "Message should be decrypted successfully");
        Assert.That(System.Text.Encoding.UTF8.GetString(bobDecrypted.Value), Is.EqualTo("Hello from Alice!"), 
            "Decrypted message should match original plaintext");
            
        // Bob sends a response to Alice
        var bobMessage = new Plaintext(System.Text.Encoding.UTF8.GetBytes("Hello from Bob!"));
        var bobEncrypted = await _bobSessionManager.EncryptMessageAsync(_sessionId, bobMessage);
        
        // Alice receives Bob's message
        var aliceDecrypted = await _aliceSessionManager.ReceiveMessageAsync(_sessionId, bobEncrypted.Value.encryptedMessage);
        
        // Verify that the message was properly decrypted
        Assert.That(aliceDecrypted, Is.Not.Null, "Message should be decrypted successfully");
        Assert.That(System.Text.Encoding.UTF8.GetString(aliceDecrypted.Value), Is.EqualTo("Hello from Bob!"), 
            "Decrypted message should match original plaintext");
            
        // Verify that session states have been updated correctly
        Assert.That(aliceSessionState!.RootKey, Is.Not.EqualTo(aliceInitialRootKey), 
            "Alice's root key should have changed after ratchet");
        Assert.That(bobSessionState!.RootKey, Is.Not.EqualTo(bobInitialRootKey), 
            "Bob's root key should have changed after ratchet");
        
            
        // Verify that previous chain length has been updated
        Assert.That(aliceSessionState!.PreviousChainLength, Is.EqualTo(1), 
            "Alice should have updated previous chain length after ratchet");
        Assert.That(bobSessionState!.PreviousChainLength, Is.EqualTo(0),
            "Bob's previous chain length should be 0 (he hadn't sent any messages before receiving)");
    }
    
    [Test]
    public async Task PreviousChainLength_AfterMultipleRatchets_IsCorrectlyTracked()
    {
        // Setup shared secret and ratchet keys
        var sharedSecret = new SharedSecret(new byte[32]); // Use a zero-filled shared secret for testing
        
        // Extract public keys needed for session establishment
        var bobIdentityKeyPublic = new RatchetIdentityKey(_bobKeys.IdentityAgreementKey.ExportSubjectPublicKeyInfo());
        var bobPreKeyPublic = new RatchetEphemeralKey(_bobKeys.SignedPreKey.ExportSubjectPublicKeyInfo());
        
        var aliceIdentityKeyPublic = new RatchetIdentityKey(_aliceKeys.IdentityAgreementKey.ExportSubjectPublicKeyInfo());
        
        // Setup conversation mocks
        var conversation = new Conversation(
            new ConversationId(_sessionId.Value),
            new ChannelId(Guid.NewGuid().ToByteArray()),
            new List<ParticipantId> { new ParticipantId(_alicePeerId.Value), new ParticipantId(_bobPeerId.Value) },
            new List<Message>());
            
        _aliceConversationRepository.Setup(x => x.GetByIdAsync(It.IsAny<ConversationId>()))
            .ReturnsAsync(conversation);
            
        // Mirror the conversation for Bob's perspective
        var conversationForBob = new Conversation(
            new ConversationId(_sessionId.Value),
            new ChannelId(Guid.NewGuid().ToByteArray()),
            new List<ParticipantId> { new ParticipantId(_bobPeerId.Value), new ParticipantId(_alicePeerId.Value) },
            new List<Message>());
            
        _bobConversationRepository.Setup(x => x.GetByIdAsync(It.IsAny<ConversationId>()))
            .ReturnsAsync(conversationForBob);
            
        // Track session states
        DoubleRatchetSession.DoubleRatchetSessionState? aliceSessionState = null;
        DoubleRatchetSession.DoubleRatchetSessionState? bobSessionState = null;
        
        // Setup session store mocks
        _aliceSessionStore.Setup(x => x.SetSessionStateAsync(It.IsAny<SessionId>(), It.IsAny<DoubleRatchetSession.DoubleRatchetSessionState>()))
            .Callback<SessionId, DoubleRatchetSession.DoubleRatchetSessionState>((id, state) => aliceSessionState = state)
            .Returns(Task.CompletedTask);
            
        _bobSessionStore.Setup(x => x.SetSessionStateAsync(It.IsAny<SessionId>(), It.IsAny<DoubleRatchetSession.DoubleRatchetSessionState>()))
            .Callback<SessionId, DoubleRatchetSession.DoubleRatchetSessionState>((id, state) => bobSessionState = state)
            .Returns(Task.CompletedTask);
            
        _aliceSessionStore.Setup(x => x.GetSessionStateAsync(It.IsAny<SessionId>()))
            .ReturnsAsync(() => aliceSessionState);
            
        _bobSessionStore.Setup(x => x.GetSessionStateAsync(It.IsAny<SessionId>()))
            .ReturnsAsync(() => bobSessionState);
            
        // Establish the initial sessions
        await _aliceSessionManager.EstablishSessionAsInitiatorAsync(
            _sessionId, 
            _bobPeerId, 
            bobIdentityKeyPublic, 
            bobPreKeyPublic, 
            sharedSecret);
            
        await _bobSessionManager.EstablishSessionAsResponderAsync(
            _sessionId, 
            _alicePeerId, 
            aliceIdentityKeyPublic, 
            _bobKeys.SignedPreKey, 
            sharedSecret);
            
        // Capture the initial states
        var initialAliceState = aliceSessionState;
        var initialBobState = bobSessionState;
        
        Assert.That(initialAliceState, Is.Not.Null, "Initial Alice session state should not be null");
        Assert.That(initialBobState, Is.Not.Null, "Initial Bob session state should not be null");
        
        // Initially previous chain length should be 0
        Assert.That(initialAliceState!.PreviousChainLength, Is.EqualTo(0), 
            "Alice's initial previous chain length should be 0");
        Assert.That(initialBobState!.PreviousChainLength, Is.EqualTo(0), 
            "Bob's initial previous chain length should be 0");
            
        // Track chain length evolution through multiple message exchanges
        ulong expectedAlicePreviousChainLength = 0;
        ulong expectedBobPreviousChainLength = 0;
        
        // Multiple rounds of message exchanges to trigger ratcheting
        for (int i = 1; i <= 3; i++)
        {
            // Alice sends message to Bob
            var aliceMessage = new Plaintext(System.Text.Encoding.UTF8.GetBytes($"Alice message {i}"));
            var aliceEncrypted = await _aliceSessionManager.EncryptMessageAsync(_sessionId, aliceMessage);
            Assert.That(aliceEncrypted, Is.Not.Null, $"Round {i}: Alice should be able to encrypt message");
            
            var aliceBeforeDecrypt = bobSessionState!.PreviousChainLength;
            
            // Bob receives Alice's message
            var bobDecrypted = await _bobSessionManager.ReceiveMessageAsync(_sessionId, aliceEncrypted.Value.encryptedMessage);
            Assert.That(bobDecrypted, Is.Not.Null, $"Round {i}: Bob should be able to decrypt Alice's message");
            
            // After first ratchet, Bob should update previous chain length
            if (i == 1)
            {
                expectedBobPreviousChainLength = 0; // Bob hasn't sent any messages yet in first round
            }
            
            Assert.That(bobSessionState!.PreviousChainLength, Is.EqualTo(expectedBobPreviousChainLength), 
                $"Round {i}: Bob's previous chain length after receiving Alice's message");
            
            // Bob sends reply to Alice
            var bobMessage = new Plaintext(System.Text.Encoding.UTF8.GetBytes($"Bob message {i}"));
            var bobEncrypted = await _bobSessionManager.EncryptMessageAsync(_sessionId, bobMessage);
            Assert.That(bobEncrypted, Is.Not.Null, $"Round {i}: Bob should be able to encrypt message");
            
            var bobBeforeDecrypt = aliceSessionState!.PreviousChainLength;
            
            // Alice receives Bob's message
            var aliceDecrypted = await _aliceSessionManager.ReceiveMessageAsync(_sessionId, bobEncrypted.Value.encryptedMessage);
            Assert.That(aliceDecrypted, Is.Not.Null, $"Round {i}: Alice should be able to decrypt Bob's message");
            
            // Update expected previous chain length for next round
            // In the Double Ratchet protocol, previousChainLength is updated during DH ratchet and equals
            // the number of messages sent in the previous sending chain (not the current round number)
            expectedAlicePreviousChainLength = 1; // Alice always sends one message before Bob replies
            
            // After Bob's first message (in round 1), when Alice receives it,
            // her previous chain length should record how many messages she has sent
            Assert.That(aliceSessionState!.PreviousChainLength, Is.EqualTo(expectedAlicePreviousChainLength), 
                $"Round {i}: Alice's previous chain length after receiving Bob's message should be {expectedAlicePreviousChainLength}");
            
            // Update expected previous chain length for next round - now it's Bob's turn
            // Each round, Bob sends exactly one message before Alice replies
            expectedBobPreviousChainLength = 1;
        }
        
        // Final verification after multiple ratchets
        Assert.That(aliceSessionState!.PreviousChainLength, Is.EqualTo(1), 
            "Alice's final previous chain length should equal the number of messages she sent in her previous sending chain (1)");
            
        Assert.That(bobSessionState!.PreviousChainLength, Is.EqualTo(1), 
            "Bob's final previous chain length should equal the number of messages he sent in his previous sending chain (1)");
    }
    
    [Test]
    public async Task OutOfOrderMessages_AreHandledCorrectly()
    {
        // Setup shared secret and ratchet keys
        var sharedSecret = new SharedSecret(new byte[32]);
        
        // Extract public keys needed for session establishment
        var bobIdentityKeyPublic = new RatchetIdentityKey(_bobKeys.IdentityAgreementKey.ExportSubjectPublicKeyInfo());
        var bobPreKeyPublic = new RatchetEphemeralKey(_bobKeys.SignedPreKey.ExportSubjectPublicKeyInfo());
        var aliceIdentityKeyPublic = new RatchetIdentityKey(_aliceKeys.IdentityAgreementKey.ExportSubjectPublicKeyInfo());
        
        // Setup conversation mocks
        var conversation = new Conversation(
            new ConversationId(_sessionId.Value),
            new ChannelId(Guid.NewGuid().ToByteArray()),
            new List<ParticipantId> { new ParticipantId(_alicePeerId.Value), new ParticipantId(_bobPeerId.Value) },
            new List<Message>());
            
        _aliceConversationRepository.Setup(x => x.GetByIdAsync(It.IsAny<ConversationId>()))
            .ReturnsAsync(conversation);
            
        // Mirror the conversation for Bob's perspective
        var conversationForBob = new Conversation(
            new ConversationId(_sessionId.Value),
            new ChannelId(Guid.NewGuid().ToByteArray()),
            new List<ParticipantId> { new ParticipantId(_bobPeerId.Value), new ParticipantId(_alicePeerId.Value) },
            new List<Message>());
            
        _bobConversationRepository.Setup(x => x.GetByIdAsync(It.IsAny<ConversationId>()))
            .ReturnsAsync(conversationForBob);
            
        // Track session states
        DoubleRatchetSession.DoubleRatchetSessionState? aliceSessionState = null;
        DoubleRatchetSession.DoubleRatchetSessionState? bobSessionState = null;
        
        // Setup session store mocks
        _aliceSessionStore.Setup(x => x.SetSessionStateAsync(It.IsAny<SessionId>(), It.IsAny<DoubleRatchetSession.DoubleRatchetSessionState>()))
            .Callback<SessionId, DoubleRatchetSession.DoubleRatchetSessionState>((id, state) => aliceSessionState = state)
            .Returns(Task.CompletedTask);
            
        _bobSessionStore.Setup(x => x.SetSessionStateAsync(It.IsAny<SessionId>(), It.IsAny<DoubleRatchetSession.DoubleRatchetSessionState>()))
            .Callback<SessionId, DoubleRatchetSession.DoubleRatchetSessionState>((id, state) => bobSessionState = state)
            .Returns(Task.CompletedTask);
            
        _aliceSessionStore.Setup(x => x.GetSessionStateAsync(It.IsAny<SessionId>()))
            .ReturnsAsync(() => aliceSessionState);
            
        _bobSessionStore.Setup(x => x.GetSessionStateAsync(It.IsAny<SessionId>()))
            .ReturnsAsync(() => bobSessionState);
            
        // Establish the initial sessions
        await _aliceSessionManager.EstablishSessionAsInitiatorAsync(
            _sessionId, 
            _bobPeerId, 
            bobIdentityKeyPublic, 
            bobPreKeyPublic, 
            sharedSecret);
            
        await _bobSessionManager.EstablishSessionAsResponderAsync(
            _sessionId, 
            _alicePeerId, 
            aliceIdentityKeyPublic,
            _bobKeys.SignedPreKey, 
            sharedSecret);
            
        // 1. Alice encrypts 3 messages in sequence
        var aliceMessage1 = new Plaintext(System.Text.Encoding.UTF8.GetBytes("Message 1"));
        var aliceEncrypted1 = await _aliceSessionManager.EncryptMessageAsync(_sessionId, aliceMessage1);
        
        var aliceMessage2 = new Plaintext(System.Text.Encoding.UTF8.GetBytes("Message 2"));
        var aliceEncrypted2 = await _aliceSessionManager.EncryptMessageAsync(_sessionId, aliceMessage2);
        
        var aliceMessage3 = new Plaintext(System.Text.Encoding.UTF8.GetBytes("Message 3"));
        var aliceEncrypted3 = await _aliceSessionManager.EncryptMessageAsync(_sessionId, aliceMessage3);
        
        // 2. Bob receives message 3 first (out of order)
        var bobDecrypted3 = await _bobSessionManager.ReceiveMessageAsync(_sessionId, aliceEncrypted3.Value.encryptedMessage);
        
        // Verify message 3 was decrypted successfully
        Assert.That(bobDecrypted3, Is.Not.Null, "Bob should be able to decrypt message 3");
        Assert.That(System.Text.Encoding.UTF8.GetString(bobDecrypted3.Value), Is.EqualTo("Message 3"), 
            "Message 3 content should be decrypted correctly");
        
        // 3. Bob receives message 1 (out of order)
        var bobDecrypted1 = await _bobSessionManager.ReceiveMessageAsync(_sessionId, aliceEncrypted1.Value.encryptedMessage);
        
        // Verify message 1 was decrypted successfully
        Assert.That(bobDecrypted1, Is.Not.Null, "Bob should be able to decrypt message 1 even though it's out of order");
        Assert.That(System.Text.Encoding.UTF8.GetString(bobDecrypted1.Value), Is.EqualTo("Message 1"), 
            "Message 1 content should be decrypted correctly");
            
        // 4. Bob receives message 2 (out of order)
        var bobDecrypted2 = await _bobSessionManager.ReceiveMessageAsync(_sessionId, aliceEncrypted2.Value.encryptedMessage);
        
        // Verify message 2 was decrypted successfully
        Assert.That(bobDecrypted2, Is.Not.Null, "Bob should be able to decrypt message 2 even though it's out of order");
        Assert.That(System.Text.Encoding.UTF8.GetString(bobDecrypted2.Value), Is.EqualTo("Message 2"), 
            "Message 2 content should be decrypted correctly");
        
        // 5. Verify that Bob's session has the correct state after handling out-of-order messages
        Assert.That(bobSessionState!.PreviousChainLength, Is.EqualTo(0), 
            "Bob's previous chain length should be 0 as no new ratchet has occurred");
            
        // 6. Bob sends a response to trigger a ratchet
        var bobMessage = new Plaintext(System.Text.Encoding.UTF8.GetBytes("Response from Bob"));
        var bobEncrypted = await _bobSessionManager.EncryptMessageAsync(_sessionId, bobMessage);
        
        // Alice receives Bob's message
        var aliceDecrypted = await _aliceSessionManager.ReceiveMessageAsync(_sessionId, bobEncrypted.Value.encryptedMessage);
        
        // Verify Bob's message was decrypted successfully
        Assert.That(aliceDecrypted, Is.Not.Null, "Alice should be able to decrypt Bob's response");
        Assert.That(System.Text.Encoding.UTF8.GetString(aliceDecrypted.Value), Is.EqualTo("Response from Bob"), 
            "Bob's response content should be decrypted correctly");
            
        // 7. After ratchet, verify previous chain length is correctly updated
        Assert.That(aliceSessionState!.PreviousChainLength, Is.EqualTo(3), 
            "Alice's previous chain length should be 3 (the number of messages she sent)");
    }
}
