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

namespace Percolator.ApplicationTests.Sessions;

[TestFixture]
public class DirectSessionManagerTests
{
    private Mock<IDoubleRatchetSessionStore> _mockSessionStore = null!;
    private Mock<IConversationStore> _mockConversationStore = null!;
    private Mock<IMessageStore> _mockMessageStore = null!;
    private Mock<ActiveIdentityContext> _mockActiveIdentityContext = null!;
    private DirectSessionManager _manager = null!;

    private X509Certificate2 _localCertificate = null!;
    private X509Certificate2 _remoteCertificate = null!;

    [SetUp]
    public void Setup()
    {
        _mockSessionStore = new Mock<IDoubleRatchetSessionStore>();
        _mockConversationStore = new Mock<IConversationStore>();
        _mockMessageStore = new Mock<IMessageStore>();
        _mockActiveIdentityContext = new Mock<ActiveIdentityContext>();

        _localCertificate = CertificateGenerator.CreateSelfSignedCertificate("CN=Local");
        _remoteCertificate = CertificateGenerator.CreateSelfSignedCertificate("CN=Remote");

        _mockActiveIdentityContext.Setup(c => c.Certificate).Returns(_localCertificate);
        _mockActiveIdentityContext.Setup(c => c.IdentityName).Returns(Guid.NewGuid().ToString());

        _manager = new DirectSessionManager(
            _mockSessionStore.Object,
            _mockConversationStore.Object,
            _mockMessageStore.Object,
            _mockActiveIdentityContext.Object
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
        using var remoteIdentityKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var sharedSecret = localIdentityKey.DeriveKeyFromHash(remoteIdentityKey.PublicKey, HashAlgorithmName.SHA256);
        var doubleRatchetSession = new DoubleRatchetSession(new SharedSecret(sharedSecret), remoteIdentityKey.PublicKey.ToByteArray());
        var sessionState = doubleRatchetSession.GetState();

        _mockSessionStore.Setup(s => s.GetSessionStateAsync(remotePeerId, conversationId))
            .ReturnsAsync(sessionState);

        var localPeerId = new SessionPeerId(Guid.Parse(_mockActiveIdentityContext.Object.IdentityName!));
        var conversation = new DirectConversation(conversationId, localPeerId, remotePeerId);
        _mockConversationStore.Setup(s => s.GetConversationAsync(conversationId)).ReturnsAsync(conversation);

        // Act
        var result = await _manager.SendDirectMessageAsync(conversationId, plaintext);

        // Assert
        result.Should().NotBeNull();
        _mockSessionStore.Verify(s => s.SaveSessionStateAsync(remotePeerId, conversationId, It.Is<DoubleRatchetSession.DoubleRatchetSessionState>(state => state.RootKey.Length > 0)), Times.Once);
        _mockMessageStore.Verify(m => m.StoreDirectMessageAsync(It.Is<DirectMessage>(msg => msg.ConversationId == conversationId)), Times.Once);
    }
}
