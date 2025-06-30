using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.Sessions;
using Percolator.Cryptography;
using Percolator.Sessions;
using Percolator.Identity;
using SessionPeerId = Percolator.Sessions.PeerId;

namespace Percolator.ApplicationTests.Sessions;

[TestFixture]
public class DirectSessionManagerTests
{
    private Mock<IDoubleRatchetSessionStore> _mockSessionStore = null!;
    private Mock<IConversationStore> _mockConversationStore = null!;
    private Mock<IMessageStore> _mockMessageStore = null!;
    private ActiveIdentityContext _activeIdentityContext = null!;
    private DirectSessionManager _manager = null!;

    private X509Certificate2 _localCertificate = null!;
    private X509Certificate2 _remoteCertificate = null!;

    [SetUp]
    public void Setup()
    {
        _mockSessionStore = new Mock<IDoubleRatchetSessionStore>();
        _mockConversationStore = new Mock<IConversationStore>();
        _mockMessageStore = new Mock<IMessageStore>();
        _activeIdentityContext = new ActiveIdentityContext();

        _localCertificate = CertificateGenerator.CreateSelfSignedCertificate("CN=Local");
        _remoteCertificate = CertificateGenerator.CreateSelfSignedCertificate("CN=Remote");

        _activeIdentityContext.Certificate = _localCertificate;
        _activeIdentityContext.IdentityName = Guid.NewGuid().ToString();

        _manager = new DirectSessionManager(
            _mockSessionStore.Object,
            _mockConversationStore.Object,
            _mockMessageStore.Object,
            _activeIdentityContext
        );
    }

    [TearDown]
    public void TearDown()
    {
        _localCertificate.Dispose();
        _remoteCertificate.Dispose();
    }

    [Test]
    public async Task SendMessageAsync_WithValidSession_EncryptsAndSavesMessage()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var remotePeerId = new SessionPeerId(Guid.NewGuid());
        var plaintext = "Hello, world!";

        using var localIdentityKey = _localCertificate.GetECDHKeyPair();
        using var remoteIdentityKey = _remoteCertificate.GetECDHKeyPair();
        var remoteIdentityPublicKeyBytes = remoteIdentityKey.PublicKey.ExportSubjectPublicKeyInfo();

        using var remoteRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var remoteRatchetPublicKeyBytes = remoteRatchetKey.PublicKey.ExportSubjectPublicKeyInfo();

        var sharedSecret = new byte[32];
        RandomNumberGenerator.Fill(sharedSecret);

        var doubleRatchetSession = DoubleRatchetSession.AsInitiator(
            sharedSecret,
            localIdentityKey,
            remoteIdentityPublicKeyBytes,
            remoteRatchetPublicKeyBytes
        );
        var sessionState = doubleRatchetSession.GetState();

        _mockSessionStore.Setup(s => s.GetSessionStateAsync(remotePeerId, conversationId))
            .ReturnsAsync(sessionState);

        var localPeerId = new SessionPeerId(Guid.Parse(_activeIdentityContext.IdentityName!));
        var conversation = new DirectConversation(conversationId, localPeerId, remotePeerId);
        _mockConversationStore.Setup(s => s.GetConversationAsync(conversationId)).ReturnsAsync(conversation);

        // Act
        var result = await _manager.SendMessageAsync(conversationId, plaintext);

        // Assert
        result.Should().NotBeNull();
        _mockSessionStore.Verify(s => s.SaveSessionStateAsync(remotePeerId, conversationId, It.Is<DoubleRatchetSession.DoubleRatchetSessionState>(state => state.RootKey.Length > 0)), Times.Once);
        _mockMessageStore.Verify(m => m.StoreDirectMessageAsync(It.Is<DirectMessage>(msg => msg.ConversationId == conversationId)), Times.Once);
    }
}
