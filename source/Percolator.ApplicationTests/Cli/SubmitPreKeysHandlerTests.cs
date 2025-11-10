using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Cli;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;

namespace Percolator.ApplicationTests.Cli;

[TestFixture]
public class SubmitPreKeysHandlerTests
{
    private ActiveIdentityContext _activeIdentity = null!;
    private Mock<IConversationService> _conversationService = null!;
    private Mock<IDirectSessionManager> _sessionManager = null!;
    private Mock<IMessageTransportService> _transport = null!;
    private Mock<IPeerRepository> _peerRepository = null!;
    private Mock<IOneTimeKeyProvider> _oneTimeKeyProvider = null!;
    private Mock<Percolator.Application.KeyExchange.ISelfPreKeyBundleRepository> _selfPreKeyRepo = null!;

    [SetUp]
    public void SetUp()
    {
        _activeIdentity = new ActiveIdentityContext();
        _conversationService = new Mock<IConversationService>();
        _sessionManager = new Mock<IDirectSessionManager>();
        _transport = new Mock<IMessageTransportService>();
        _peerRepository = new Mock<IPeerRepository>();
        _oneTimeKeyProvider = new Mock<IOneTimeKeyProvider>();
        _selfPreKeyRepo = new Mock<Percolator.Application.KeyExchange.ISelfPreKeyBundleRepository>();
    }

    [Test]
    public async Task Handle_ReturnsZero_OnSuccess()
    {
        // Arrange identity context
        var selfPeerId = Guid.NewGuid();
        var identity = new Percolator.Identity.Model.IdentityRecord(selfPeerId, "self") { SelfIdentityId = 1 };
        var ikSigning = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var spk = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _activeIdentity.Identity = identity;
        _activeIdentity.Keys = new X3dhKeys(ikSigning, spk);

        // Arrange peer and session
        var remotePeerId = new Percolator.Identity.PeerId(Guid.NewGuid());
        var remotePeer = new Peer(remotePeerId, "bob");
        _peerRepository.Setup(r => r.GetByNameAsync("bob")).ReturnsAsync(remotePeer);
        var directSessionId = new DirectSessionId(Guid.NewGuid());
        _conversationService
            .Setup(s => s.GetExistingDirectSessionAsync(It.IsAny<Peer>()))
            .ReturnsAsync(directSessionId);

        // One-time key provider returns fresh keys for signed pre-key and N one-time keys
        _oneTimeKeyProvider.Setup(p => p.PopOneTimeKey())
            .Returns(() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

        // Session manager encrypt returns a dummy ratchet message
        _sessionManager
            .Setup(m => m.EncryptMessageAsync(new SessionId(directSessionId.Value), It.IsAny<Plaintext>()))
            .ReturnsAsync(new SessionRatchetMessage(new byte[] { 1, 2, 3 }));

        // Build a fake response envelope bytes (SubmitPreKeyBundleResponse)
        var responseEnvelope = new InternalEnvelope
        {
            SubmitPreKeyBundleResponse = new SubmitPreKeyBundleResponse { Version = 1 }
        };
        var responseBytes = responseEnvelope.ToByteArray();

        // Transport returns a response payload (opaque); the session manager will "decrypt" it
        _transport
            .Setup(t => t.SendMessageAsync(remotePeerId, directSessionId, It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeliverOpaqueMessageResponse
            {
                Version = 1,
                ResponsePayload = new DeliverOpaqueMessageResponse.Types.Payload
                {
                    Version = 1,
                    ResponsePayload = ByteString.CopyFrom(responseBytes)
                }
            });

        _sessionManager
            .Setup(m => m.ReceiveMessageAsync(new SessionId(directSessionId.Value), It.IsAny<SessionRatchetMessage>()))
            .ReturnsAsync(new Plaintext(responseBytes));

        // Act
        var identityAdapter = new PeerIdentityRepositoryAdapter(_peerRepository.Object);
        var handler = new SubmitPreKeysHandler(
            new NullLogger<SubmitPreKeysHandler>(),
            _conversationService.Object,
            _sessionManager.Object,
            _transport.Object,
            _activeIdentity,
            identityAdapter,
            _oneTimeKeyProvider.Object,
            _selfPreKeyRepo.Object);

        var cmd = new SubmitPreKeysCommand(
            TargetPeerName: "bob",
            OneTimeKeyCount: 3,
            ExpiresUtc: DateTimeOffset.UtcNow.AddDays(7));

        var rc = await handler.Handle(cmd, CancellationToken.None);

        // Assert
        rc.Should().Be(0);
        _transport.Verify(t => t.SendMessageAsync(remotePeerId, directSessionId, It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        _sessionManager.Verify(m => m.ReceiveMessageAsync(new SessionId(directSessionId.Value), It.IsAny<SessionRatchetMessage>()), Times.Once);
    }

    [Test]
    public void Handle_Throws_OnZeroCount()
    {
        // Arrange minimal identity
        var identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = 1 };
        _activeIdentity.Identity = identity;
        _activeIdentity.Keys = new X3dhKeys(
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

        var identityAdapter = new PeerIdentityRepositoryAdapter(_peerRepository.Object);
        var handler = new SubmitPreKeysHandler(
            new NullLogger<SubmitPreKeysHandler>(),
            _conversationService.Object,
            _sessionManager.Object,
            _transport.Object,
            _activeIdentity,
            identityAdapter,
            _oneTimeKeyProvider.Object,
            _selfPreKeyRepo.Object);

        var cmd = new SubmitPreKeysCommand(
            TargetPeerName: "bob",
            OneTimeKeyCount: 0,
            ExpiresUtc: DateTimeOffset.UtcNow.AddDays(1));

        // Act/Assert
        Assert.ThrowsAsync<ArgumentException>(() => handler.Handle(cmd, CancellationToken.None));
    }
}
