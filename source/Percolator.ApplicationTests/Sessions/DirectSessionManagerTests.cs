using System.Security.Cryptography;
using Moq;
using Percolator.Application.Identity;
using Percolator.Application.Sessions;
using Percolator.Identity.Model;
using Percolator.Sessions;
using Percolator.Identity;

namespace Percolator.ApplicationTests.Sessions;

[TestFixture]
public class DirectSessionManagerTests
{
    private Mock<IDoubleRatchetSessionStore> _mockSessionStore = null!;
    private Mock<IConversationStore> _mockConversationStore = null!;
    private Mock<IMessageStore> _mockMessageStore = null!;
    private ActiveIdentityContext _activeIdentityContext = null!;
    private DirectSessionManager _manager = null!;

    private X3dhKeys _localKeys = null!;
    private ECDiffieHellman _remoteIdentityKey = null!;

    [SetUp]
    public void Setup()
    {
        _mockSessionStore = new Mock<IDoubleRatchetSessionStore>();
        _mockConversationStore = new Mock<IConversationStore>();
        _mockMessageStore = new Mock<IMessageStore>();
        _activeIdentityContext = new ActiveIdentityContext();

        var identity = new IdentityRecord(Guid.NewGuid(), "Local Identity");
        var identitySigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identityAgreementKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var signedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var oneTimePreKeys = new[] { ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256) };
        _localKeys = new X3dhKeys(identitySigningKey, identityAgreementKey, signedPreKey, oneTimePreKeys);

        _activeIdentityContext.Identity = identity;
        _activeIdentityContext.Keys = _localKeys;

        _remoteIdentityKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

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
        _localKeys.Dispose();
        _remoteIdentityKey.Dispose();
    }
}
