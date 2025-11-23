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
using Percolator.Application.Services;
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;

namespace Percolator.ApplicationTests.Cli;

[TestFixture]
public class SubmitPreKeysHandlerTests
{
    private ActiveIdentityContext _activeIdentity = null!;
    private Mock<IDirectSessionLocator> _directSessionLocator = null!;
    private Mock<ISecureMessagingService> _secureSvc;
    private Mock<IMessageTransportService> _transport = null!;
    private Mock<Percolator.Identity.IPeerIdentityRepository> _peerIdentityRepository = null!;
    private Mock<IOneTimeKeyProvider> _oneTimeKeyProvider = null!;
    private Mock<Percolator.Application.KeyExchange.ISelfPreKeyBundleRepository> _selfPreKeyRepo = null!;

    [SetUp]
    public void SetUp()
    {
        _activeIdentity = new ActiveIdentityContext();
        _directSessionLocator = new Mock<IDirectSessionLocator>();
        _transport = new Mock<IMessageTransportService>();
        _peerIdentityRepository = new Mock<Percolator.Identity.IPeerIdentityRepository>();
        _oneTimeKeyProvider = new Mock<IOneTimeKeyProvider>();
        _selfPreKeyRepo = new Mock<Percolator.Application.KeyExchange.ISelfPreKeyBundleRepository>();
        _secureSvc = new Mock<ISecureMessagingService>(MockBehavior.Strict);
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
        _peerIdentityRepository
            .Setup(r => r.GetByNameAsync(It.IsAny<Percolator.Identity.Model.DisplayName>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Percolator.Identity.Model.DisplayName dn, CancellationToken _) =>
            {
                var id = new Percolator.Identity.Model.PeerIdentity(remotePeerId);
                id.SetDisplayName(dn);
                return id;
            });
        var directSessionId = new DirectSessionId(Guid.NewGuid());
        _directSessionLocator
            .Setup(s => s.GetAsync(It.IsAny<Percolator.Identity.PeerId>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(directSessionId);

        // One-time key provider returns fresh keys for signed pre-key and N one-time keys
        _oneTimeKeyProvider.Setup(p => p.PopOneTimeKey())
            .Returns(() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

        // Secure messaging encrypt returns a dummy ratchet message
        _secureSvc
            .Setup(s => s.EncryptAsync(It.IsAny<SessionId>(), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionRatchetMessage(new byte[] { 1, 2, 3 }));

        // Build a fake response envelope bytes (SubmitPreKeyBundleResponse)
        var responseEnvelope = new InternalEnvelope
        {
            SubmitPreKeyBundleResponse = new SubmitPreKeyBundleResponse { Version = 1 }
        };
        var responseBytes = responseEnvelope.ToByteArray();

        // Transport returns a response payload (opaque); the secure messaging service will decrypt it
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

        _secureSvc
            .Setup(s => s.DecryptInboundAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SessionId(directSessionId.Value), new Plaintext(responseBytes)));

        // Act
        var handler = new SubmitPreKeysHandler(
            new NullLogger<SubmitPreKeysHandler>(),
            _directSessionLocator.Object,
            _transport.Object,
            _activeIdentity,
            _peerIdentityRepository.Object,
            _oneTimeKeyProvider.Object,
            _selfPreKeyRepo.Object,
            _secureSvc.Object);

        var cmd = new SubmitPreKeysCommand(
            TargetPeerName: "bob",
            OneTimeKeyCount: 3,
            ExpiresUtc: DateTimeOffset.UtcNow.AddDays(7));

        var rc = await handler.Handle(cmd, CancellationToken.None);

        // Assert
        rc.Should().Be(0);
        _transport.Verify(t => t.SendMessageAsync(remotePeerId, directSessionId, It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        _secureSvc.Verify(s => s.DecryptInboundAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()), Times.Once);
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

        var handler = new SubmitPreKeysHandler(
            new NullLogger<SubmitPreKeysHandler>(),
            _directSessionLocator.Object,
            _transport.Object,
            _activeIdentity,
            _peerIdentityRepository.Object,
            _oneTimeKeyProvider.Object,
            _selfPreKeyRepo.Object,
            _secureSvc.Object);

        var cmd = new SubmitPreKeysCommand(
            TargetPeerName: "bob",
            OneTimeKeyCount: 0,
            ExpiresUtc: DateTimeOffset.UtcNow.AddDays(1));

        // Act/Assert
        Assert.ThrowsAsync<ArgumentException>(() => handler.Handle(cmd, CancellationToken.None));
    }
}
