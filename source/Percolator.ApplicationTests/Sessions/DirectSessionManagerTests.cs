using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.Sessions;
using Percolator.Cryptography;
using Percolator.Identity.Model;
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
        var oneTimePreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _localKeys = new X3dhKeys(identitySigningKey, identityAgreementKey, signedPreKey, oneTimePreKey);

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

    [Test]
    public async Task SendMessageAsync_WithValidSession_EncryptsAndSavesMessage()
    {
        // Arrange
        var conversationId = new ConversationId(Guid.NewGuid());
        var remotePeerId = new SessionPeerId(Guid.NewGuid());
        var plaintext = "Hello, world!";

        var remoteIdentityPublicKeyBytes = _remoteIdentityKey.PublicKey.ExportSubjectPublicKeyInfo();

        using var remoteRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var remoteRatchetPublicKeyBytes = remoteRatchetKey.PublicKey.ExportSubjectPublicKeyInfo();

        var sharedSecret = new byte[32];
        RandomNumberGenerator.Fill(sharedSecret);

        using var localIdentityKeyCopy = ECDiffieHellman.Create(_localKeys.IdentityAgreementKey.ExportParameters(true));
        var doubleRatchetSession = DoubleRatchetSession.AsInitiator(
            sharedSecret,
            localIdentityKeyCopy,
            remoteIdentityPublicKeyBytes,
            remoteRatchetPublicKeyBytes
        );
        var sessionState = doubleRatchetSession.GetState();

        _mockSessionStore.Setup(s => s.GetSessionStateAsync(remotePeerId, conversationId))
            .ReturnsAsync(sessionState);

        var localPeerId = new SessionPeerId(_activeIdentityContext.Identity!.Id);
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
