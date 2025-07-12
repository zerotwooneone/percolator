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

namespace Percolator.ApplicationTests.Sessions;

[TestFixture]
public class MessageServiceTests
{
    private MessageService _messageService = null!;
    private DirectSessionManager _sessionManager = null!;
    private ActiveIdentityContext _activeIdentityContext = null!;
    private FileBasedDoubleRatchetSessionStore _sessionStore = null!;

    private Mock<IConversationRepository> _mockConversationRepository = null!;
    private Mock<IPeerRepository> _mockPeerRepository = null!;
    private Mock<IMessageStore> _mockMessageStore = null!;
    private Mock<IMessageTransportService> _mockTransportService = null!;
    private string _tempDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _mockConversationRepository = new Mock<IConversationRepository>();
        _mockPeerRepository = new Mock<IPeerRepository>();
        _mockMessageStore = new Mock<IMessageStore>();
        _mockTransportService = new Mock<IMessageTransportService>();
        _activeIdentityContext = new ActiveIdentityContext();

        _tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDirectory);
        var storageOptions = Options.Create(new StorageOptions { Path = _tempDirectory });
        _sessionStore = new FileBasedDoubleRatchetSessionStore(storageOptions);

        _sessionManager = new DirectSessionManager(
            _sessionStore,
            _mockConversationRepository.Object,
            _mockMessageStore.Object,
            _activeIdentityContext);

        _messageService = new MessageService(
            _mockMessageStore.Object,
            _sessionManager,
            _mockTransportService.Object,
            _activeIdentityContext);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
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
        var conversation = new ChatConversation(conversationId, new ChannelId(new byte[64]), participants.ToList(), new List<Message>());

        var localDhKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var sessionState = new DoubleRatchetSession.DoubleRatchetSessionState
        {
            RootKey = new RootKey(new byte[32]),
            SendingCounter = 0,
            ReceivingCounter = 0,
            TheirIdentityPublicKey = new RatchetIdentityKey(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256).PublicKey.ExportSubjectPublicKeyInfo()),
            TheirDhRatchetPublicKey = new RatchetEphemeralKey(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256).PublicKey.ExportSubjectPublicKeyInfo()),
            DhRatchetPrivateKey = new PrivateEphemeralKey(localDhKey.ExportECPrivateKey()),
            SkippedMessageKeys = new Dictionary<ulong, MessageKey>()
        };

        _mockConversationRepository.Setup(r => r.GetByIdAsync(It.Is<ChatConversationId>(c => c.Value == conversationId.Value)))
            .ReturnsAsync(conversation);

        await _sessionStore.SaveSessionStateAsync(remotePeerId, new SessionConversationId(conversationId.Value), sessionState);

        // Act
        await _messageService.SendDirectMessageAsync(conversationId, "Hello");

        // Assert
        _mockTransportService.Verify(t => t.SendMessageAsync(
            It.IsAny<IdentityPeerId>(),
            It.IsAny<ChatConversationId>(),
            It.IsAny<RatchetMessage>()), Times.Once);
    }
}
