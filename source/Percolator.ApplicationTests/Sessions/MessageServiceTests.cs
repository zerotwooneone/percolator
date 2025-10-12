using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Application.Network;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using IdentityPeerId = Percolator.Identity.PeerId;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;
using DirectSessionId = Percolator.Network.DirectSessionId;

namespace Percolator.ApplicationTests.Sessions;

[TestFixture]
public class MessageServiceTests
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
    private Mock<IConversationRepository> _mockConversationRepository = null!;
    private Mock<IMessageTransportService> _mockTransportService = null!;
    private Mock<IDoubleRatchetSessionStore> _mockSessionStore = null!;
    private ActiveIdentityContext _activeIdentityContext = null!;
    private DirectSessionManager _sessionManager = null!;
    private MessageService _messageService = null!;
    private IOptions<CryptographyOptions> _options = null!;
    private Mock<IDirectSessionRepository> _mockDirectSessionRepository;

    [SetUp]
    public void SetUp()
    {
        _mockConversationRepository = new Mock<IConversationRepository>();
        _mockTransportService = new Mock<IMessageTransportService>();
        _activeIdentityContext = new ActiveIdentityContext();
        _mockSessionStore = new Mock<IDoubleRatchetSessionStore>();
        _options = Options.Create(new CryptographyOptions());
        _mockDirectSessionRepository = new Mock<IDirectSessionRepository>();

        // Create a logger factory for DirectSessionManager
        var loggerFactory = new NullLoggerFactory();

        var ratchetLookup = new Moq.Mock<IRatchetKeySessionLookup>();
        _sessionManager = new DirectSessionManager(
            _mockSessionStore.Object,
            _activeIdentityContext,
            new NullLogger<DirectSessionManager>(),
            loggerFactory,
            _options,
            ratchetLookup.Object,
            new FakePreHandshakeStore());

        _messageService = new MessageService(
            _sessionManager,
            _mockTransportService.Object,
            _mockConversationRepository.Object,
            new NullLogger<MessageService>(),
            _activeIdentityContext,
            _mockSessionStore.Object,
            _mockDirectSessionRepository.Object);

        // Default transport behavior for tests: return a response when sending
        _mockTransportService
            .Setup(t => t.SendMessageAsync(
                It.IsAny<IdentityPeerId>(),
                It.IsAny<DirectSessionId>(),
                It.IsAny<SessionRatchetMessage>(),
                It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new Percolator.Contracts.DeliverOpaqueMessageResponse { Version = 1 });
    }

    [TearDown]
    public void TearDown()
    {
    }

    [Test]
    public async Task SendDirectMessageAsync_WithValidSession_ShouldSucceed()
    {
        // Arrange
        var localIdentity = new IdentityRecord(Guid.NewGuid(), "Local Identity") { SelfIdentityId = 1 };
        var localKeys = new X3dhKeys(
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));
        _activeIdentityContext.Identity = localIdentity;
        _activeIdentityContext.Keys = localKeys;

        
        var remotePeerId = new IdentityPeerId(Guid.NewGuid());
        var directSessionId = new DirectSessionId(Guid.NewGuid());
        var participants = new[]
        {
            new ChatParticipantId(localIdentity.Id),
            new ChatParticipantId(remotePeerId.Value)
        };
        var conversation = new Conversation(new ChatConversationId(directSessionId.Value), participants.ToList(), new List<Message>());
        
        // Create a valid dummy session state with proper cryptographic keys
        // Generate proper EC keys using nistP256 curve as used in the actual implementation
        using var dhRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var dhPrivateKeyBytes = dhRatchetKey.ExportECPrivateKey();
        var dhPublicKeyBytes = dhRatchetKey.PublicKey.ExportSubjectPublicKeyInfo();
        
        // Create a second key for the remote identity
        using var remoteIdentityKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var remotePublicKeyBytes = remoteIdentityKey.PublicKey.ExportSubjectPublicKeyInfo();

        var dummySessionState = new DoubleRatchetSession.DoubleRatchetSessionState
        {
            RootKey = new RootKey(new byte[32]), // Root key can be all zeros for test
            SendingCounter = 0,
            ReceivingCounter = 0,
            TheirIdentityPublicKey = new RatchetIdentityKey(remotePublicKeyBytes),
            TheirDhRatchetPublicKey = new PreKey(dhPublicKeyBytes),
            DhRatchetPrivateKey = new PrivateEphemeralKey(dhPrivateKeyBytes),
            SkippedMessageKeys = new Dictionary<SkippedMessageKeyIdentifier, byte[]>()
        };

        _mockConversationRepository.Setup(r => r.GetByIdAsync(It.Is<ChatConversationId>(c => c.Value == directSessionId.Value), It.IsAny<int>()))
            .ReturnsAsync(conversation);

        // Create the SessionId to match how MessageService creates it (directly from conversationId.Value)
        var sessionId = new SessionId(directSessionId.Value);
        _mockSessionStore.Setup(s => s.GetSessionStateAsync(sessionId, localIdentity.SelfIdentityId)).ReturnsAsync(dummySessionState);

        // Act
        await _messageService.SendDirectMessageAsync(directSessionId, "Hello", remotePeerId);

        // Assert
        _mockTransportService.Verify(t => t.SendMessageAsync(
            It.IsAny<IdentityPeerId>(),
            It.IsAny<DirectSessionId>(),
            It.IsAny<SessionRatchetMessage>(),
            It.IsAny<System.Threading.CancellationToken>()), Times.Once);
    }
}
