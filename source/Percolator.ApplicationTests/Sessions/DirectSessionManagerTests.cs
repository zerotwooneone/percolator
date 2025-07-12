using System.Security.Cryptography;
using Moq;
using Percolator.Application.Identity;
using Percolator.Application.Sessions;
using Percolator.Identity.Model;
using Percolator.Sessions;
using Percolator.Chat;
using Percolator.Identity;

namespace Percolator.ApplicationTests.Sessions;

[TestFixture]
public class DirectSessionManagerTests
{
    private Mock<IDoubleRatchetSessionStore> _mockSessionStore = null!;
    private Mock<IConversationRepository> _mockConversationRepository = null!;
    private Mock<IPeerRepository> _mockPeerRepository = null!;
    private Mock<IMessageStore> _mockMessageStore = null!;
    private Mock<IDoubleRatchetProtocol> _mockProtocol = null!;
    private Mock<ActiveIdentityContext> _mockActiveIdentityContext = null!;
    private DirectSessionManager _sessionManager = null!;

    private X3dhKeys _localKeys = null!;
    private ECDiffieHellman _remoteIdentityKey = null!;

    [SetUp]
    public void Setup()
    {        
        _mockSessionStore = new Mock<IDoubleRatchetSessionStore>();
        _mockConversationRepository = new Mock<IConversationRepository>();
        _mockPeerRepository = new Mock<IPeerRepository>();
        _mockMessageStore = new Mock<IMessageStore>();
        _mockProtocol = new Mock<IDoubleRatchetProtocol>();
        _mockActiveIdentityContext = new Mock<ActiveIdentityContext>();

        var identity = new IdentityRecord(Guid.NewGuid(), "Local Identity");
        var identitySigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identityAgreementKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var signedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _localKeys = new X3dhKeys(identitySigningKey, identityAgreementKey, signedPreKey);

        _remoteIdentityKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        
        _sessionManager = new DirectSessionManager(
            _mockSessionStore.Object,
            _mockConversationRepository.Object,
            _mockMessageStore.Object,
            _mockActiveIdentityContext.Object,
            _mockProtocol.Object
        );
    }

    [TearDown]
    public void TearDown()
    {
        _localKeys.Dispose();
        _remoteIdentityKey.Dispose();
    }

    [Test]
    public void Test1()
    {
        Assert.Pass();
    }
}
