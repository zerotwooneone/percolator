using System;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.Network.Handshake;
using Percolator.Application.Sessions;
using Percolator.Application.Services;
using Percolator.ApplicationTests.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using Percolator.Application.KeyExchange;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public class HandleHandshakeInitiatorHelloCommandTests
{
    [Test]
    public async Task Sets_RelayPeerId_when_provided_and_creates_connection_if_missing()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<HandleHandshakeInitiatorHelloHandler>.Instance;
        var pkhStore = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Loose);
        var selfPre = new Mock<ISelfPreKeyBundleRepository>(MockBehavior.Loose);
        var directRepo = new Mock<IDirectSessionRepository>(MockBehavior.Loose);
        var secure = new Mock<ISecureMessagingService>(MockBehavior.Loose);
        var activeAccessor = Mock.Of<IActiveIdentityAccessor>(a => a.IsActive == true);
        var active = new ActiveIdentityContext { Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "me", null) { SelfIdentityId = new SelfId(1) } };
        var peerIdentityRepo = new Mock<Percolator.Identity.IPeerIdentityRepository>(MockBehavior.Loose);
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

        // Active keys required by handler
        using var ik1 = System.Security.Cryptography.ECDiffieHellman.Create();
        using var spk1 = System.Security.Cryptography.ECDiffieHellman.Create();
        active.Keys = new X3dhKeys(ik1, spk1);
        
        // Handler encrypts responder inner hello via SecureMessagingService
        secure.Setup(s => s.EncryptAsync(It.IsAny<SessionId>(), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
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
        // New dependency for handler: ISessionCrypto
        var sessionCrypto = new Mock<ISessionCrypto>(MockBehavior.Loose);
        sessionCrypto.Setup(s => s.X3DH_Respond(
                It.IsAny<RatchetIdentityKey>(),
                It.IsAny<RatchetEphemeralKey>(),
                It.IsAny<PrivatePreKey>(),
                It.IsAny<PrivatePreKey>(),
                It.IsAny<PrivatePreKey?>()))
            .Returns(new SharedSecret(new byte[32]));
        var sessionRepo = new Mock<ISessionRepository>(MockBehavior.Loose);
        sessionRepo.Setup(r => r.AddAsync(It.IsAny<SecureSession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new HandleHandshakeInitiatorHelloHandler(
            logger, 
            pkhStore.Object, 
            selfPre.Object, 
            directRepo.Object, 
            secure.Object, 
            activeAccessor,
            active, 
            peerIdentityRepo.Object, 
            profileRepo.Object, 
            mediator.Object,
            sessionCrypto.Object,
            sessionRepo.Object,
            new TestClock());
        var cmd = new HandleHandshakeInitiatorHelloCommand(spki, eph, spkId, null, null, null, RelayHostPeerId: relayHost);
        _ = await sut.Handle(cmd, CancellationToken.None);

        Assert.That(saved, Is.Not.Null);
        Assert.That(saved!.Relays.Count, Is.EqualTo(1));
        sessionRepo.Verify(r => r.AddAsync(It.IsAny<SecureSession>(), It.IsAny<CancellationToken>()), Times.Once);
        // Critical side-effects
        pkhStore.Verify(k => k.ActivateIfChangedAsync(
            It.IsAny<Percolator.Identity.PeerId>(),
            It.IsAny<byte[]>(),
            It.IsAny<byte[]>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        peerIdentityRepo.Verify(r => r.SaveAsync(It.IsAny<Percolator.Identity.Model.PeerIdentity>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        profileRepo.Verify(r => r.UpsertAsync(It.IsAny<PeerRoutingProfile>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        sessionCrypto.Verify(s => s.X3DH_Respond(
            It.IsAny<RatchetIdentityKey>(),
            It.IsAny<RatchetEphemeralKey>(),
            It.IsAny<PrivatePreKey>(),
            It.IsAny<PrivatePreKey>(),
            It.IsAny<PrivatePreKey?>()), Times.Once);
    }

    [Test]
    public async Task Updates_existing_connection_to_set_RelayPeerId()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<HandleHandshakeInitiatorHelloHandler>.Instance;
        var pkhStore = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Strict);
        var selfPre = new Mock<ISelfPreKeyBundleRepository>(MockBehavior.Strict);
        var directRepo = new Mock<IDirectSessionRepository>(MockBehavior.Strict);
        var secure = new Mock<ISecureMessagingService>(MockBehavior.Strict);
        var activeAccessor = Mock.Of<IActiveIdentityAccessor>(a => a.IsActive == true);
        var active = new ActiveIdentityContext { Identity = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "me", null) { SelfIdentityId = new SelfId(1) } };
        var peerIdentityRepo = new Mock<Percolator.Identity.IPeerIdentityRepository>(MockBehavior.Strict);
        var profileRepo = new Mock<IPeerRoutingProfileRepository>(MockBehavior.Loose);
        var mediator = new Mock<MediatR.IMediator>(MockBehavior.Loose);

        var spki = new byte[] { 1 };
        var eph = new byte[] { 2 };
        var spkId = Guid.NewGuid();
        Percolator.Identity.PeerId relayHost = new Percolator.Identity.PeerId(Guid.NewGuid());

        selfPre.Setup(r => r.TryGetSignedPreKeyAsync(1, spkId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new byte[] { 9 }, new byte[] { 10 }, new byte[] { 11 }, DateTimeOffset.UtcNow.AddHours(1)));
        // Active keys required by handler
        using var ik2 = System.Security.Cryptography.ECDiffieHellman.Create();
        using var spk2 = System.Security.Cryptography.ECDiffieHellman.Create();
        active.Keys = new X3dhKeys(ik2, spk2);
        secure.Setup(s => s.EncryptAsync(It.IsAny<SessionId>(), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
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
        // New dependency for handler: ISessionCrypto
        var sessionCrypto2 = new Mock<ISessionCrypto>(MockBehavior.Loose);
        sessionCrypto2.Setup(s => s.X3DH_Respond(
                It.IsAny<RatchetIdentityKey>(),
                It.IsAny<RatchetEphemeralKey>(),
                It.IsAny<PrivatePreKey>(),
                It.IsAny<PrivatePreKey>(),
                It.IsAny<PrivatePreKey?>()))
            .Returns(new SharedSecret(new byte[32]));
        var sessionRepo = new Mock<ISessionRepository>(MockBehavior.Loose);
        sessionRepo.Setup(r => r.AddAsync(It.IsAny<SecureSession>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new HandleHandshakeInitiatorHelloHandler(
            logger, 
            pkhStore.Object, 
            selfPre.Object, 
            directRepo.Object, 
            secure.Object, 
            activeAccessor,
            active, 
            peerIdentityRepo.Object, 
            profileRepo.Object, 
            mediator.Object,
            sessionCrypto2.Object,
            sessionRepo.Object,
            new TestClock());
        var cmd = new HandleHandshakeInitiatorHelloCommand(spki, eph, spkId, null, null, null, RelayHostPeerId: relayHost);
        _ = await sut.Handle(cmd, CancellationToken.None);

        Assert.That(existing.Relays.Count, Is.EqualTo(1));
        Assert.That(existing.Relays[0].RelayPeerId.Value, Is.EqualTo(relayHost.Value));
        sessionRepo.Verify(r => r.AddAsync(It.IsAny<SecureSession>(), It.IsAny<CancellationToken>()), Times.Once);
        // Critical side-effects
        pkhStore.Verify(k => k.ActivateIfChangedAsync(
            It.IsAny<Percolator.Identity.PeerId>(),
            It.IsAny<byte[]>(),
            It.IsAny<byte[]>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        peerIdentityRepo.Verify(r => r.SaveAsync(It.IsAny<Percolator.Identity.Model.PeerIdentity>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        profileRepo.Verify(r => r.UpsertAsync(It.IsAny<PeerRoutingProfile>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        sessionCrypto2.Verify(s => s.X3DH_Respond(
            It.IsAny<RatchetIdentityKey>(),
            It.IsAny<RatchetEphemeralKey>(),
            It.IsAny<PrivatePreKey>(),
            It.IsAny<PrivatePreKey>(),
            It.IsAny<PrivatePreKey?>()), Times.Once);
    }
}
