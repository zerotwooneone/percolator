using System.Security.Cryptography;
using Moq;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Chat;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Sessions;
using ChatConversation = Percolator.Chat.Conversation;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;
using IdentityPeerId = Percolator.Identity.PeerId;
using SessionConversationId = Percolator.Sessions.ConversationId;
using SessionPeerId = Percolator.Sessions.PeerId;
using Percolator.Infrastructure.Sessions;
using Microsoft.Extensions.Options;
using Percolator.Chat.ValueObjects;
using Percolator.Infrastructure;
using System.Text.Json;
using SessionState = Percolator.Sessions.SessionState;
using SessionRatchetMessage = Percolator.Sessions.RatchetMessage;
using CryptoRatchetIdentityKey = Percolator.Cryptography.RatchetIdentityKey;
using CryptoRatchetEphemeralKey = Percolator.Cryptography.RatchetEphemeralKey;
using CryptoPrivateEphemeralKey = Percolator.Cryptography.PrivateEphemeralKey;
using CryptoRootKey = Percolator.Cryptography.RootKey;
using CryptoMessageKey = Percolator.Cryptography.MessageKey;

namespace Percolator.ApplicationTests.Sessions;

[TestFixture]
public class MessageServiceTests
{
    private MessageService _messageService = null!;
    private DirectSessionManager _sessionManager = null!;
    private ActiveIdentityContext _activeIdentityContext = null!;
    private Mock<IDoubleRatchetSessionStore> _mockSessionStore = null!;

    private Mock<IConversationRepository> _mockConversationRepository = null!;
    private Mock<IPeerRepository> _mockPeerRepository = null!;
    private Mock<IMessageStore> _mockMessageStore = null!;
    private Mock<IMessageTransportService> _mockTransportService = null!;
    private Mock<IDoubleRatchetProtocol> _mockProtocol = null!;

    [SetUp]
    public void SetUp()
    {
        _mockConversationRepository = new Mock<IConversationRepository>();
        _mockPeerRepository = new Mock<IPeerRepository>();
        _mockMessageStore = new Mock<IMessageStore>();
        _mockTransportService = new Mock<IMessageTransportService>();
        _activeIdentityContext = new ActiveIdentityContext();
        _mockSessionStore = new Mock<IDoubleRatchetSessionStore>();
        _mockProtocol = new Mock<IDoubleRatchetProtocol>();

        _sessionManager = new DirectSessionManager(
            _mockSessionStore.Object,
            _mockConversationRepository.Object,
            _mockMessageStore.Object,
            _activeIdentityContext,
            _mockProtocol.Object);

        _messageService = new MessageService(
            _mockMessageStore.Object,
            _sessionManager,
            _mockTransportService.Object,
            _activeIdentityContext);
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
        var conversation = new ChatConversation(conversationId, new ChannelId(remotePeerId.Value.ToByteArray()), participants.ToList(), new List<Message>());
        var localDhKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var cryptoSessionState = new DoubleRatchetSession.DoubleRatchetSessionState
        {
            RootKey = new CryptoRootKey(new byte[32]),
            SendingCounter = 0,
            ReceivingCounter = 0,
            TheirIdentityPublicKey = new CryptoRatchetIdentityKey(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256).PublicKey.ExportSubjectPublicKeyInfo()),
            TheirDhRatchetPublicKey = new CryptoRatchetEphemeralKey(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256).PublicKey.ExportSubjectPublicKeyInfo()),
            DhRatchetPrivateKey = new CryptoPrivateEphemeralKey(localDhKey.ExportECPrivateKey()),
            SkippedMessageKeys = new Dictionary<ulong, CryptoMessageKey>()
        };
        var sessionState = new SessionState(JsonSerializer.SerializeToUtf8Bytes(cryptoSessionState));

        _mockConversationRepository.Setup(r => r.GetByIdAsync(It.Is<ChatConversationId>(c => c.Value == conversationId.Value)))
            .ReturnsAsync(conversation);

        var sessionId = $"{remotePeerId.Value}-{conversationId.Value}";
        _mockSessionStore.Setup(s => s.GetSessionStateAsync(sessionId)).ReturnsAsync(sessionState);

        // Act
        await _messageService.SendDirectMessageAsync(conversationId,  "Hello");

        // Assert
        _mockTransportService.Verify(t => t.SendMessageAsync(
            It.IsAny<IdentityPeerId>(),
            It.IsAny<ChatConversationId>(),
            It.IsAny<SessionRatchetMessage>()), Times.Once);
    }
}
