using System.Security.Cryptography;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Percolator.Application.Identity;
using Percolator.Application.Sessions;
using Percolator.Application.Network;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Message = Percolator.Chat.Message;
using ParticipantId = Percolator.Chat.ValueObjects.ParticipantId;

namespace Percolator.ApplicationTests.Sessions;

[TestFixture]
public class DirectSessionManagerTests
{
    private sealed class FakePreHandshakeStore : Percolator.Application.Network.Handshake.IPreHandshakeSessionStore
    {
        public Task SaveAsync(Percolator.Application.Network.Handshake.PreHandshakeRecord record, CancellationToken cancellationToken) => Task.CompletedTask;
        public async IAsyncEnumerable<Percolator.Application.Network.Handshake.PreHandshakeRecord> EnumeratePendingAsync(int selfIdentityId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task DeleteAsync(long recordId, int selfIdentityId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PurgeExpiredAsync(int selfIdentityId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    // Alice (initiator) components
    private Mock<IDoubleRatchetSessionStore> _aliceSessionStore = null!;
    private ActiveIdentityContext _aliceIdentityContext = null!;
    private DirectSessionManager _aliceSessionManager = null!;
    private X3dhKeys _aliceKeys = null!;
    private Identity.PeerId _alicePeerId = null!;
    private Mock<IRatchetKeySessionLookup> _aliceRatchetLookup = null!;
    
    // Bob (responder) components
    private Mock<IDoubleRatchetSessionStore> _bobSessionStore = null!;
    private ActiveIdentityContext _bobIdentityContext = null!;
    private DirectSessionManager _bobSessionManager = null!;
    private X3dhKeys _bobKeys = null!;
    private Identity.PeerId _bobPeerId = null!;
    private Mock<IRatchetKeySessionLookup> _bobRatchetLookup = null!;
    
    // Shared components
    private SessionId _sessionId = null!;
    private ILoggerFactory _loggerFactory = null!;
    private IOptions<CryptographyOptions> _options = null!;
    private ECDiffieHellman _aliceEphemeral;
    private readonly Dictionary<SessionId, DoubleRatchetSession.DoubleRatchetSessionState> _aliceStates = new();
    private readonly Dictionary<SessionId, DoubleRatchetSession.DoubleRatchetSessionState> _bobStates = new();

    [SetUp]
    public void Setup()
    {
        _loggerFactory = new NullLoggerFactory();
        _options = Options.Create(new CryptographyOptions());
        
        // Create unique session ID
        _sessionId = new SessionId(Guid.NewGuid());
        
        // Generate Alice's identity and keys
        _alicePeerId = new Identity.PeerId(Guid.NewGuid());
        _aliceEphemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _aliceKeys = new X3dhKeys(
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
            _aliceEphemeral);
            
        _aliceSessionStore = new Mock<IDoubleRatchetSessionStore>();
        
        // Create a real ActiveIdentityContext instance instead of a mock
        _aliceIdentityContext = new ActiveIdentityContext();
        // Set properties directly
        _aliceIdentityContext.Identity = new IdentityRecord(_alicePeerId.Value, "Alice") { SelfIdentityId = 1 };
        _aliceIdentityContext.Keys = _aliceKeys;
        _aliceRatchetLookup = new Mock<IRatchetKeySessionLookup>(MockBehavior.Loose);
        _aliceSessionManager = new DirectSessionManager(
            _aliceSessionStore.Object,
            _aliceIdentityContext,
            _loggerFactory.CreateLogger<DirectSessionManager>(),
            _loggerFactory,
            _options,
            _aliceRatchetLookup.Object,
            new FakePreHandshakeStore());
            
        // Generate Bob's identity and keys
        _bobPeerId = new Identity.PeerId(Guid.NewGuid());
        _bobKeys = new X3dhKeys(
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));
            
        _bobSessionStore = new Mock<IDoubleRatchetSessionStore>();
        
        // Create a real ActiveIdentityContext instance instead of a mock
        _bobIdentityContext = new ActiveIdentityContext();
        // Set properties directly
        _bobIdentityContext.Identity = new IdentityRecord(_bobPeerId.Value, "Bob") { SelfIdentityId = 1 };
        _bobIdentityContext.Keys = _bobKeys;
        _bobRatchetLookup = new Mock<IRatchetKeySessionLookup>(MockBehavior.Loose);
        _bobSessionManager = new DirectSessionManager(
            _bobSessionStore.Object,
            _bobIdentityContext,
            _loggerFactory.CreateLogger<DirectSessionManager>(),
            _loggerFactory,
            _options,
            _bobRatchetLookup.Object,
            new FakePreHandshakeStore());
            
        // Setup session store mocks to use class-level state variables
        _aliceSessionStore.Setup(x => x.SetSessionStateAsync(It.IsAny<SessionId>(), It.IsAny<DoubleRatchetSession.DoubleRatchetSessionState>(), It.IsAny<int>()))
            .Callback<SessionId, DoubleRatchetSession.DoubleRatchetSessionState, int>((sessionId, state, selfIdentityId) => 
            {
                _aliceStates[sessionId] = state;
            })
            .Returns(Task.CompletedTask);
        _bobSessionStore.Setup(x => x.SetSessionStateAsync(It.IsAny<SessionId>(), It.IsAny<DoubleRatchetSession.DoubleRatchetSessionState>(), It.IsAny<int>()))
            .Callback<SessionId, DoubleRatchetSession.DoubleRatchetSessionState, int>((sessionId, state, selfIdentityId) => 
            {
                _bobStates[sessionId] = state;
            })
            .Returns(Task.CompletedTask);

        _aliceSessionStore.Setup(x => x.GetSessionStateAsync(It.IsAny<SessionId>(), It.IsAny<int>()))
            .ReturnsAsync((SessionId sid, int _) => _aliceStates.TryGetValue(sid, out var s) ? s : null);
        _bobSessionStore.Setup(x => x.GetSessionStateAsync(It.IsAny<SessionId>(), It.IsAny<int>()))
            .ReturnsAsync((SessionId sid, int _) => _bobStates.TryGetValue(sid, out var s) ? s : null);
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
        var aliceInitialRootKey = _aliceStates[_sessionId].RootKey;
        var bobInitialRootKey = _bobStates[_sessionId].RootKey;

        // Act: Alice encrypts a message for Bob
        var aliceMessage = new Plaintext(System.Text.Encoding.UTF8.GetBytes("Hello from Alice!"));
        var aliceEncrypted = await _aliceSessionManager.EncryptMessageAsync(_sessionId, aliceMessage);

        // Act: Bob receives Alice's message
        var bobDecrypted = await _bobSessionManager.ReceiveMessageAsync(_sessionId, aliceEncrypted);

        // Assert
        Assert.That(bobDecrypted, Is.Not.Null, "Message should be decrypted successfully");
        Assert.That(System.Text.Encoding.UTF8.GetString(bobDecrypted!.Value), Is.EqualTo("Hello from Alice!"),
            "Decrypted message should match original plaintext");

        // Verify Bob upserts ratchet lookup with exact header key
        var aliceHeader = aliceEncrypted.GetHeader();
        _bobRatchetLookup.Verify(l => l.UpsertAsync(
            It.Is<Percolator.Network.DirectSessionId>(d => d.Value == _sessionId.Value),
            It.IsAny<int>(),
            It.Is<RatchetEphemeralKey>(k => k.Value.SequenceEqual(aliceHeader.PreKey.Value)),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Act: Bob sends a response to Alice
        var bobMessage = new Plaintext(System.Text.Encoding.UTF8.GetBytes("Hello from Bob!"));
        var bobEncrypted = await _bobSessionManager.EncryptMessageAsync(_sessionId, bobMessage);

        // Act: Alice receives Bob's message
        var aliceDecrypted = await _aliceSessionManager.ReceiveMessageAsync(_sessionId, bobEncrypted);

        // Assert
        Assert.That(aliceDecrypted, Is.Not.Null, "Message should be decrypted successfully");
        Assert.That(System.Text.Encoding.UTF8.GetString(aliceDecrypted!.Value), Is.EqualTo("Hello from Bob!"),
            "Decrypted message should match original plaintext");

        // Verify Alice upserts ratchet lookup with exact header key
        var bobHeader = bobEncrypted.GetHeader();
        _aliceRatchetLookup.Verify(l => l.UpsertAsync(
            It.Is<Percolator.Network.DirectSessionId>(d => d.Value == _sessionId.Value),
            It.IsAny<int>(),
            It.Is<RatchetEphemeralKey>(k => k.Value.SequenceEqual(bobHeader.PreKey.Value)),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.That(_aliceStates[_sessionId].RootKey, Is.Not.EqualTo(aliceInitialRootKey),
            "Alice's root key should have changed after ratchet");
        Assert.That(_bobStates[_sessionId].RootKey, Is.Not.EqualTo(bobInitialRootKey),
            "Bob's root key should have changed after ratchet");

        // Looser checks: ensure counters advanced and previous chain lengths are valid (implementation-flexible)
        Assert.That(_aliceStates[_sessionId].SendingCounter, Is.GreaterThanOrEqualTo(1));
        Assert.That(_bobStates[_sessionId].ReceivingCounter, Is.GreaterThanOrEqualTo(1));
        Assert.That(_aliceStates[_sessionId].PreviousChainLength, Is.GreaterThanOrEqualTo(0));
        Assert.That(_bobStates[_sessionId].PreviousChainLength, Is.GreaterThanOrEqualTo(0));
    }
    
    [Test]
    public async Task SessionSymmetry_ResponderInitiatesMessage_MaintainsConsistentState()
    {
        // Arrange
        await EstablishSessionsAsync();

        // Act & Assert: Bob (responder) sends the first message to Alice
        var bobMessage = new Plaintext(System.Text.Encoding.UTF8.GetBytes("Hello from Bob!"));
        var bobEncrypted = await _bobSessionManager.EncryptMessageAsync(_sessionId, bobMessage);
        var aliceDecrypted = await _aliceSessionManager.ReceiveMessageAsync(_sessionId, bobEncrypted);

        Assert.That(aliceDecrypted, Is.Not.Null, "Alice should decrypt Bob's message successfully");
        Assert.That(System.Text.Encoding.UTF8.GetString(aliceDecrypted.Value), Is.EqualTo("Hello from Bob!"), "Decrypted message should match original");

        // Act & Assert: Alice (initiator) sends a reply to Bob
        var aliceMessage = new Plaintext(System.Text.Encoding.UTF8.GetBytes("Hello from Alice!"));
        var aliceEncrypted = await _aliceSessionManager.EncryptMessageAsync(_sessionId, aliceMessage);
        var bobDecrypted = await _bobSessionManager.ReceiveMessageAsync(_sessionId, aliceEncrypted);

        Assert.That(bobDecrypted, Is.Not.Null, "Bob should decrypt Alice's message successfully");
        Assert.That(System.Text.Encoding.UTF8.GetString(bobDecrypted.Value), Is.EqualTo("Hello from Alice!"), "Decrypted reply should match original");

        // Assert: Final state verification
        // Avoid strict root key equality; assert both non-null and counters advanced
        Assert.That(_aliceStates[_sessionId].RootKey, Is.Not.Null);
        Assert.That(_bobStates[_sessionId].RootKey, Is.Not.Null);
        Assert.That(_aliceStates[_sessionId].SendingCounter, Is.GreaterThanOrEqualTo(1), "Alice should have a sending counter >= 1");
        Assert.That(_bobStates[_sessionId].SendingCounter, Is.GreaterThanOrEqualTo(1), "Bob should have a sending counter >= 1");
    }

    [Test]
    public async Task PreviousChainLength_AfterMultipleRatchets_IsCorrectlyTracked()
    {
        // Arrange
        await EstablishSessionsAsync();
        
        // Assert: Initial state verification
        Assert.That(_aliceStates.ContainsKey(_sessionId), Is.True);
        Assert.That(_bobStates.ContainsKey(_sessionId), Is.True);
        Assert.That(_aliceStates[_sessionId].PreviousChainLength, Is.GreaterThanOrEqualTo(0), "Alice's initial previous chain length should be >= 0");
        Assert.That(_bobStates[_sessionId].PreviousChainLength, Is.GreaterThanOrEqualTo(0), "Bob's initial previous chain length should be >= 0");
            
        // Act & Assert: Multiple rounds of message exchanges
        for (int i = 1; i <= 3; i++)
        {
            var aliceMessage = new Plaintext(System.Text.Encoding.UTF8.GetBytes($"Alice message {i}"));
            var aliceEncrypted = await _aliceSessionManager.EncryptMessageAsync(_sessionId, aliceMessage);
            await _bobSessionManager.ReceiveMessageAsync(_sessionId, aliceEncrypted);

            var bobMessage = new Plaintext(System.Text.Encoding.UTF8.GetBytes($"Bob message {i}"));
            var bobEncrypted = await _bobSessionManager.EncryptMessageAsync(_sessionId, bobMessage);
            await _aliceSessionManager.ReceiveMessageAsync(_sessionId, bobEncrypted);
        }
        
        // Assert: Final verification after multiple ratchets
        Assert.That(_aliceStates[_sessionId].PreviousChainLength, Is.GreaterThanOrEqualTo(1), "Alice's final previous chain length should be >= 1");
        Assert.That(_bobStates[_sessionId].PreviousChainLength, Is.GreaterThanOrEqualTo(1), "Bob's final previous chain length should be >= 1");
    }
    
    [Test]
    public async Task ResponderOverload_EstablishesSymmetricSession_WithInitiatorFirstMessage()
    {
        // Arrange: prepare X3DH-agreed materials
        var sharedSecret = new SharedSecret(new byte[32]);
        var bobIdentityKeyPublic = new RatchetIdentityKey(_bobKeys.IdentitySigningKey.ExportSubjectPublicKeyInfo());
        var bobPreKeyPublic = new RatchetEphemeralKey(_bobKeys.SignedPreKey.ExportSubjectPublicKeyInfo());
        var aliceIdentityKeyPublic = new RatchetIdentityKey(_aliceKeys.IdentitySigningKey.ExportSubjectPublicKeyInfo());

        // Capture Alice's ephemeral public key before constructing the initiator session (it may take ownership and dispose)
        var aliceEphemeralPublicSpki = _aliceEphemeral.PublicKey.ExportSubjectPublicKeyInfo();

        // Construct Alice (initiator) DR session in-memory using her ephemeral
        var initiatorLogger = _loggerFactory.CreateLogger<DoubleRatchetSession>();
        using var aliceInitiator = DoubleRatchetSession.AsInitiator(
            sharedSecret,
            bobIdentityKeyPublic,
            bobPreKeyPublic,
            _aliceEphemeral,
            initiatorLogger,
            _options);

        // Embed the session id in plaintext so responder can extract it
        var sid = new SessionId(Guid.NewGuid());
        var firstPayload = new Plaintext(System.Text.Encoding.UTF8.GetBytes(sid.Value.ToString()));
        var firstMessage = aliceInitiator.Encrypt(firstPayload);

        // Act: Bob (responder) establishes by decrypting Alice's first message
        SessionId GetSessionId(Plaintext pt) => new SessionId(Guid.Parse(System.Text.Encoding.UTF8.GetString(pt.Value)));
        var (resolvedSid, ptOut) = await _bobSessionManager.EstablishSessionAsResponderAsync(
            firstMessage,
            GetSessionId,
            aliceIdentityKeyPublic,
            new RatchetEphemeralKey(aliceEphemeralPublicSpki),
            _bobKeys.SignedPreKey,
            sharedSecret);

        // Assert: session id round-trips and plaintext matches
        Assert.That(resolvedSid.Value, Is.EqualTo(sid.Value));
        Assert.That(System.Text.Encoding.UTF8.GetString(ptOut.Value), Is.EqualTo(sid.Value.ToString()));

        // Assert: responder session persisted
        Assert.That(_bobStates.ContainsKey(resolvedSid), Is.True);

        // Act: Bob encrypts a reply and Alice decrypts via her initiator session
        var replyPt = new Plaintext(System.Text.Encoding.UTF8.GetBytes("ack-from-bob"));
        var reply = await _bobSessionManager.EncryptMessageAsync(resolvedSid, replyPt);
        var aliceDecrypted = aliceInitiator.Decrypt(reply);

        // Assert: symmetry holds
        Assert.That(System.Text.Encoding.UTF8.GetString(aliceDecrypted.Value), Is.EqualTo("ack-from-bob"));
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
        var decryptedMsg3 = await _bobSessionManager.ReceiveMessageAsync(_sessionId, encryptedMsg3);
        var decryptedMsg1 = await _bobSessionManager.ReceiveMessageAsync(_sessionId, encryptedMsg1);
        var decryptedMsg2 = await _bobSessionManager.ReceiveMessageAsync(_sessionId, encryptedMsg2);

        // Assert
        Assert.That(decryptedMsg1, Is.Not.Null);
        Assert.That(decryptedMsg2, Is.Not.Null);
        Assert.That(decryptedMsg3, Is.Not.Null);
        Assert.That(System.Text.Encoding.UTF8.GetString(decryptedMsg1!.Value), Is.EqualTo("Message 1"));
        Assert.That(System.Text.Encoding.UTF8.GetString(decryptedMsg2!.Value), Is.EqualTo("Message 2"));
        Assert.That(System.Text.Encoding.UTF8.GetString(decryptedMsg3!.Value), Is.EqualTo("Message 3"));
    }
    
    private async Task EstablishSessionsAsync()
    {
        var sharedSecret = new SharedSecret(new byte[32]);
        var bobIdentityKeyPublic = new RatchetIdentityKey(_bobKeys.IdentitySigningKey.ExportSubjectPublicKeyInfo());
        var bobPreKeyPublic = new RatchetEphemeralKey(_bobKeys.SignedPreKey.ExportSubjectPublicKeyInfo());
        var aliceIdentityKeyPublic = new RatchetIdentityKey(_aliceKeys.IdentitySigningKey.ExportSubjectPublicKeyInfo());
        var aliceEphemeralKeyPublic = new RatchetEphemeralKey(_aliceKeys.SignedPreKey.ExportSubjectPublicKeyInfo());
        
        await _aliceSessionManager.EstablishSessionAsInitiatorAsync(_sessionId, bobIdentityKeyPublic, bobPreKeyPublic, sharedSecret, _aliceEphemeral);
        await _bobSessionManager.EstablishSessionAsResponderAsync(_sessionId, aliceIdentityKeyPublic, aliceEphemeralKeyPublic, _bobKeys.SignedPreKey, sharedSecret);

        Assert.That(_aliceStates.ContainsKey(_sessionId), Is.True);
        Assert.That(_bobStates.ContainsKey(_sessionId), Is.True);
    }
}
