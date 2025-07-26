using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Sessions;
using Percolator.Cryptography.Primitives;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using SessionConversationId = Percolator.Sessions.ConversationId;
using IdentityPeerId = Percolator.Identity.PeerId;
using SessionPeerId = Percolator.Sessions.PeerId;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;
using System.Text;

namespace Percolator.ApplicationTests.Sessions;

[TestFixture]
public class MessageServiceTests
{
    private Mock<IConversationRepository> _mockConversationRepository = null!;
    private Mock<IPeerRepository> _mockPeerRepository = null!;
    private Mock<IMessageTransportService> _mockTransportService = null!;
    private Mock<IDoubleRatchetSessionStore> _mockSessionStore = null!;
    private ActiveIdentityContext _activeIdentityContext = null!;
    private DirectSessionManager _sessionManager = null!;
    private MessageService _messageService = null!;

    [SetUp]
    public void SetUp()
    {
        _mockConversationRepository = new Mock<IConversationRepository>();
        _mockPeerRepository = new Mock<IPeerRepository>();
        _mockTransportService = new Mock<IMessageTransportService>();
        _activeIdentityContext = new ActiveIdentityContext();
        _mockSessionStore = new Mock<IDoubleRatchetSessionStore>();

        _sessionManager = new DirectSessionManager(
            _mockSessionStore.Object,
            _mockConversationRepository.Object,
            _activeIdentityContext,
            new NullLogger<DirectSessionManager>());

        _messageService = new MessageService(
            _sessionManager,
            _mockTransportService.Object,
            _mockConversationRepository.Object,
            new NullLogger<MessageService>(),
            _activeIdentityContext,
            _mockSessionStore.Object);
    }

    [TearDown]
    public void TearDown()
    {
    }

    [Test]
    public async Task SendDirectMessageAsync_WithValidSession_ShouldSucceed()
    {
        // Arrange
        var localIdentity = new IdentityRecord(Guid.NewGuid(), "Local Identity");
        var localKeys = new X3dhKeys(
            ECDsa.Create(ECCurve.NamedCurves.nistP256),
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));
        _activeIdentityContext.Identity = localIdentity;
        _activeIdentityContext.Keys = localKeys;

        
        var remotePeerId = new SessionPeerId(Guid.NewGuid());
        var conversationId = new ChatConversationId(Guid.NewGuid());
        var participants = new[]
        {
            new ChatParticipantId(localIdentity.Id),
            new ChatParticipantId(remotePeerId.Value)
        };
        var conversation = new Conversation(conversationId, new ChannelId(remotePeerId.Value.ToByteArray()), participants.ToList(), new List<Message>());
        
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
            TheirDhRatchetPublicKey = new RatchetEphemeralKey(dhPublicKeyBytes),
            DhRatchetPrivateKey = new PrivateEphemeralKey(dhPrivateKeyBytes),
            SkippedMessageKeys = new Dictionary<ulong, MessageKey>()
        };

        _mockConversationRepository.Setup(r => r.GetByIdAsync(It.Is<ChatConversationId>(c => c.Value == conversationId.Value)))
            .ReturnsAsync(conversation);

        // Create the SessionId to match how MessageService creates it (directly from conversationId.Value)
        var sessionId = new SessionId(conversationId.Value);
        _mockSessionStore.Setup(s => s.GetSessionStateAsync(sessionId)).ReturnsAsync(dummySessionState);

        // Act
        await _messageService.SendDirectMessageAsync(conversationId, "Hello");

        // Assert
        _mockTransportService.Verify(t => t.SendMessageAsync(
            It.IsAny<IdentityPeerId>(),
            It.IsAny<ChatConversationId>(),
            It.IsAny<SessionRatchetMessage>()), Times.Once);
    }
}
