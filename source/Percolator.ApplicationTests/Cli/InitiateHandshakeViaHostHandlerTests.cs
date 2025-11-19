using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Cli;
using Percolator.Application.Network;
using Percolator.Application.Network.Handshake;
using Percolator.Application.Sessions;
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;

namespace Percolator.ApplicationTests.Cli;

[TestFixture]
public class InitiateHandshakeViaHostHandlerTests
{
    [Test]
    public async Task Handle_RequestsPreKeyBundle_ThenInvokesInitiatorHelloService()
    {
        // Arrange
        var logger = new NullLogger<InitiateHandshakeViaHostHandler>();
        var conversation = new Mock<IConversationService>(MockBehavior.Strict);
        var sessionManager = new Mock<IDirectSessionManager>(MockBehavior.Strict);
        var transport = new Mock<IMessageTransportService>(MockBehavior.Strict);
        var secure = new Mock<ISecureMessagingService>(MockBehavior.Strict);
        var peerIdentityRepo = new Mock<Percolator.Identity.IPeerIdentityRepository>(MockBehavior.Strict);
        var pkhStore = new Mock<IPeerPublicSigningKeyStore>(MockBehavior.Strict);
        var profileRepo = new Mock<IPeerRoutingProfileRepository>(MockBehavior.Loose);
        var initiatorService = new Mock<IInitiatorHelloService>(MockBehavior.Strict);

        // Host peer setup
        var hostPeerGuid = Guid.NewGuid();
        var hostPeerIdentity = new Percolator.Identity.Model.PeerIdentity(new Percolator.Identity.PeerId(hostPeerGuid));
        hostPeerIdentity.SetDisplayName("host");
        peerIdentityRepo.Setup(r => r.GetByNameAsync(It.IsAny<Percolator.Identity.Model.DisplayName>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Percolator.Identity.Model.DisplayName dn, CancellationToken _) => dn.Value == "host" ? hostPeerIdentity : null);

        // Direct session to host exists
        var hostDirectSession = new Percolator.Network.DirectSessionId(Guid.NewGuid());
        conversation.Setup(c => c.GetExistingDirectSessionAsync(It.IsAny<Percolator.Identity.Peer>()))
            .ReturnsAsync(hostDirectSession);

        // Prepare GetPreKeyBundle response
        var remoteIdentitySpki = new byte[] { 1, 2, 3 };
        var remotePreKeySpki = new byte[] { 4, 5, 6 };
        var spkId = Guid.NewGuid();
        var bundle = new GetPreKeyBundleResponse
        {
            Version = 1,
            PreKeyBundle = new GetPreKeyBundleResponse.Types.PreKeyBundle
            {
                Version = 1,
                IdentityKey = ByteString.CopyFrom(remoteIdentitySpki),
                SignedPreKeyId = ByteString.CopyFrom(spkId.ToByteArray()),
                SignedPreKey = ByteString.CopyFrom(remotePreKeySpki),
                PreKeySignature = ByteString.CopyFrom(new byte[] { 9 })
            }
        };
        var respEnvelope = new InternalEnvelope { GetPreKeyBundleResponse = bundle };

        // Encrypt to host (request) via secure service
        secure
            .Setup(s => s.EncryptAsync(It.IsAny<SessionId>(), It.IsAny<Plaintext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionRatchetMessage(new byte[] { 0xAB }));

        // Transport returns a response payload (cipher bytes)
        transport
            .Setup(t => t.SendMessageAsync(hostPeerIdentity.Id, hostDirectSession, It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeliverOpaqueMessageResponse
            {
                Version = 1,
                ResponsePayload = new DeliverOpaqueMessageResponse.Types.Payload
                {
                    Version = 1,
                    ResponsePayload = ByteString.CopyFrom(new byte[] { 0xCD })
                }
            });

        // Decrypt the response into our InternalEnvelope via SecureMessagingService
        secure
            .Setup(s => s.DecryptInboundAsync(It.IsAny<SessionRatchetMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SessionId(Guid.NewGuid()), new Plaintext(respEnvelope.ToByteArray())));

        // Target PKH unknown -> create/update new peer and activate PKH mapping
        pkhStore.Setup(s => s.GetPeerIdByPublicKeyHashAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Percolator.Identity.PeerId?)null);
        pkhStore.Setup(s => s.ActivateIfChangedAsync(
                It.IsAny<Percolator.Identity.PeerId?>(),
                It.IsAny<byte[]>(),
                It.IsAny<byte[]>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        peerIdentityRepo.Setup(r => r.SaveAsync(It.IsAny<Percolator.Identity.Model.PeerIdentity>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Profile repo paths
        profileRepo.Setup(r => r.GetByIdAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PeerRoutingProfile?)null);
        profileRepo.Setup(r => r.UpsertAsync(It.IsAny<PeerRoutingProfile>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Expect initiator service to be called with host peer id and parsed bundle
        initiatorService
            .Setup(s => s.SendInitiatorHelloViaHostAsync(
                It.IsAny<byte[]>(),
                It.Is<byte[]>(b => b != null && b.SequenceEqual(remoteIdentitySpki)),
                It.Is<Guid>(g => g == spkId),
                It.IsAny<Guid?>(),
                It.Is<byte[]>(b => b != null && b.SequenceEqual(remotePreKeySpki)),
                It.Is<Percolator.Identity.PeerId>(p => p.Value == hostPeerGuid),
                It.IsAny<byte[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = new InitiateHandshakeViaHostHandler(
            logger,
            conversation.Object,
            sessionManager.Object,
            secure.Object,
            transport.Object,
            peerIdentityRepo.Object,
            pkhStore.Object,
            profileRepo.Object,
            initiatorService.Object);

        var targetPkh = new byte[] { 0x99 };
        var cmd = new InitiateHandshakeViaHostCommand("host", targetPkh, PeerName: "alice", InitiatorPayload: null);

        // Act
        await handler.Handle(cmd, CancellationToken.None);

        // Assert
        initiatorService.VerifyAll();
    }
}
