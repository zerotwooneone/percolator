using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;

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
    private Mock<IDirectSessionRepository> _mockDirectSessionRepository = null!;
    private Mock<IPeerRepository> _mockPeerRepository = null!;
    private Mock<IPeerPublicSigningKeyStore> _mockKeyStore = null!;

    [SetUp]
    public void SetUp()
    {
        _mockConversationRepository = new Mock<IConversationRepository>();
        _mockTransportService = new Mock<IMessageTransportService>();
        _activeIdentityContext = new ActiveIdentityContext();
        _mockSessionStore = new Mock<IDoubleRatchetSessionStore>();
        _options = Options.Create(new CryptographyOptions());
        _mockDirectSessionRepository = new Mock<IDirectSessionRepository>();
        _mockPeerRepository = new Mock<IPeerRepository>();
        _mockKeyStore = new Mock<IPeerPublicSigningKeyStore>();

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
            new NullLogger<MessageService>(),
            _mockDirectSessionRepository.Object,
            _sessionManager,
            _mockTransportService.Object,
            _activeIdentityContext,
            _mockPeerRepository.Object,
            _mockKeyStore.Object);

        // Default transport behavior for tests: return a response when sending
        _mockTransportService
            .Setup(t => t.SendMessageAsync(
                It.IsAny<Percolator.Identity.PeerId>(),
                It.IsAny<Percolator.Network.DirectSessionId>(),
                It.IsAny<SessionRatchetMessage>(),
                It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new Percolator.Contracts.DeliverOpaqueMessageResponse { Version = 1 });
    }

    [TearDown]
    public void TearDown()
    {
    }

   
}
