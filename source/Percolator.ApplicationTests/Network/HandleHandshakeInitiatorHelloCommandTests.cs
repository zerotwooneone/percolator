using System;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Network.Handshake;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public class HandleHandshakeInitiatorHelloCommandTests
{
    [Test]
    public async Task Sets_RelayPeerId_when_provided_and_creates_connection_if_missing()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<HandleHandshakeInitiatorHelloHandler>.Instance;
        var x3dh = new Mock<IX3DHOrchestrator>(MockBehavior.Strict);
        var pkhStore = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Strict);
        var selfPre = new Mock<ISelfPreKeyBundleRepository>(MockBehavior.Strict);
        var directRepo = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
        var sessionMgr = new Mock<IDirectSessionManager>(MockBehavior.Strict);
        var active = new ActiveIdentityContext { Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "me", null) { SelfIdentityId = 1 } };
        var peerIdentityRepo = new Mock<Percolator.Identity.IPeerIdentityRepository>(MockBehavior.Strict);
        var profileRepo = new Mock<IPeerRoutingProfileRepository>(MockBehavior.Loose);
        var mediator = new Mock<MediatR.IMediator>(MockBehavior.Loose);

        // Inputs
        var spki = new byte[] { 1, 2, 3 };
        var eph = new byte[] { 4, 5, 6 };
        var spkId = Guid.NewGuid();
        Percolator.Identity.PeerId relayHost = new Percolator.Identity.PeerId(Guid.NewGuid());

        // Self pre-key lookups
        selfPre.Setup(r => r.TryGetSignedPreKeyAsync(1, spkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new byte[] { 9 }, new byte[] { 10 }, new byte[] { 11 }, DateTimeOffset.UtcNow.AddHours(1)));
        // No OTK path

        // X3DH completion
        using var ecdh = System.Security.Cryptography.ECDiffieHellman.Create();
        var bundle = new X3dPreKeyBundle(new RatchetIdentityKey(new byte[]{10}), new RatchetEphemeralKey(new byte[]{11}), null);
        x3dh.Setup(x => x.CompleteHandshake(It.IsAny<RatchetIdentityKey>(), It.IsAny<RatchetEphemeralKey>(), It.IsAny<System.Security.Cryptography.ECDiffieHellman>()))
            .Returns(new HandshakeResponse(new SharedSecret(new byte[]{2}), bundle, ecdh));

        // Session establishment
        sessionMgr.Setup(m => m.EstablishSessionAsResponderAsync(It.IsAny<SessionId>(), It.IsAny<RatchetIdentityKey>(), It.IsAny<RatchetEphemeralKey>(), It.IsAny<ECDiffieHellman>(), It.IsAny<SharedSecret>()))
            .Returns(Task.CompletedTask);
        // Handler encrypts responder inner hello
        sessionMgr.Setup(m => m.EncryptMessageAsync(It.IsAny<SessionId>(), It.IsAny<Plaintext>()))
            .ReturnsAsync(new SessionRatchetMessage(new byte[] { 0x01 }));

        // Persist identity artifacts via identity repository
        peerIdentityRepo.Setup(r => r.GetByIdAsync(It.IsAny<Percolator.Identity.PeerId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Percolator.Identity.Model.PeerIdentity?)null);
        peerIdentityRepo.Setup(r => r.SaveAsync(It.IsAny<Percolator.Identity.Model.PeerIdentity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        pkhStore.Setup(k => k.ActivateIfChangedAsync(It.IsAny<Percolator.Identity.PeerId>(), spki, It.IsAny<byte[]>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // No existing profile; UpsertAsync should be called with relay set
        profileRepo.Setup(r => r.GetByIdAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<CancellationToken>())).ReturnsAsync((PeerRoutingProfile?)null);
        PeerRoutingProfile? saved = null;
        profileRepo.Setup(r => r.UpsertAsync(It.IsAny<PeerRoutingProfile>(), It.IsAny<CancellationToken>())).Callback<PeerRoutingProfile, CancellationToken>((p, _) => saved = p).Returns(Task.CompletedTask);

        var sut = new HandleHandshakeInitiatorHelloHandler(logger, x3dh.Object, pkhStore.Object, selfPre.Object, directRepo.Object, sessionMgr.Object, active, peerIdentityRepo.Object, profileRepo.Object, mediator.Object);
        var cmd = new HandleHandshakeInitiatorHelloCommand(spki, eph, spkId, null, null, null, RelayHostPeerId: relayHost);
        _ = await sut.Handle(cmd, CancellationToken.None);

        Assert.That(saved, Is.Not.Null);
        Assert.That(saved!.Relays.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task Updates_existing_connection_to_set_RelayPeerId()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<HandleHandshakeInitiatorHelloHandler>.Instance;
        var x3dh = new Mock<IX3DHOrchestrator>(MockBehavior.Strict);
        var pkhStore = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Strict);
        var selfPre = new Mock<ISelfPreKeyBundleRepository>(MockBehavior.Strict);
        var directRepo = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
        var sessionMgr = new Mock<IDirectSessionManager>(MockBehavior.Strict);
        var active = new ActiveIdentityContext { Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "me", null) { SelfIdentityId = 1 } };
        var peerIdentityRepo = new Mock<Percolator.Identity.IPeerIdentityRepository>(MockBehavior.Strict);
        var profileRepo = new Mock<IPeerRoutingProfileRepository>(MockBehavior.Loose);
        var mediator = new Mock<MediatR.IMediator>(MockBehavior.Loose);

        var spki = new byte[] { 1 };
        var eph = new byte[] { 2 };
        var spkId = Guid.NewGuid();
        Percolator.Identity.PeerId relayHost = new Percolator.Identity.PeerId(Guid.NewGuid());

        selfPre.Setup(r => r.TryGetSignedPreKeyAsync(1, spkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new byte[] { 9 }, new byte[] { 10 }, new byte[] { 11 }, DateTimeOffset.UtcNow.AddHours(1)));
        using var ecdh3 = System.Security.Cryptography.ECDiffieHellman.Create();
        var bundle3 = new X3dPreKeyBundle(new RatchetIdentityKey(new byte[]{10}), new RatchetEphemeralKey(new byte[]{11}), null);
        x3dh.Setup(x => x.CompleteHandshake(It.IsAny<RatchetIdentityKey>(), It.IsAny<RatchetEphemeralKey>(), It.IsAny<System.Security.Cryptography.ECDiffieHellman>()))
            .Returns(new HandshakeResponse(new SharedSecret(new byte[]{2}), bundle3, ecdh3));
        sessionMgr.Setup(m => m.EstablishSessionAsResponderAsync(It.IsAny<SessionId>(), It.IsAny<RatchetIdentityKey>(), It.IsAny<RatchetEphemeralKey>(), It.IsAny<ECDiffieHellman>(), It.IsAny<SharedSecret>()))
            .Returns(Task.CompletedTask);
        sessionMgr.Setup(m => m.EncryptMessageAsync(It.IsAny<SessionId>(), It.IsAny<Plaintext>()))
            .ReturnsAsync(new SessionRatchetMessage(new byte[] { 0x02 }));
        peerIdentityRepo.Setup(r => r.SaveAsync(It.IsAny<Percolator.Identity.Model.PeerIdentity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        pkhStore.Setup(k => k.ActivateIfChangedAsync(It.IsAny<Percolator.Identity.PeerId>(), spki, It.IsAny<byte[]>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var remoteNetPeer = new Percolator.Network.PeerId(Guid.NewGuid());
        var existing = new PeerRoutingProfile();
        existing.BindIdentity(remoteNetPeer);
        profileRepo.Setup(r => r.GetByIdAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        profileRepo.Setup(r => r.UpsertAsync(existing, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        // Adapter removed; identity repo GetByIdAsync returns null by default
        peerIdentityRepo.Setup(r => r.GetByIdAsync(It.IsAny<Percolator.Identity.PeerId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Percolator.Identity.Model.PeerIdentity?)null);

        var sut = new HandleHandshakeInitiatorHelloHandler(logger, x3dh.Object, pkhStore.Object, selfPre.Object, directRepo.Object, sessionMgr.Object, active, peerIdentityRepo.Object, profileRepo.Object, mediator.Object);
        var cmd = new HandleHandshakeInitiatorHelloCommand(spki, eph, spkId, null, null, null, RelayHostPeerId: relayHost);
        _ = await sut.Handle(cmd, CancellationToken.None);

        Assert.That(existing.Relays.Count, Is.EqualTo(1));
        Assert.That(existing.Relays[0].RelayPeerId.Value, Is.EqualTo(relayHost.Value));
    }
}
