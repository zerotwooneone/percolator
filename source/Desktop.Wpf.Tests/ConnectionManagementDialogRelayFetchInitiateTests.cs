using System;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Sessions.Commands;
using Desktop.Wpf.Features.Sessions.Queries;
using FluentAssertions;
using Google.Protobuf;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Network.Handshake;
using Percolator.Application.Services;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Network;
using Microsoft.Extensions.DependencyInjection;

namespace Desktop.Wpf.Tests;

[TestFixture]
[Apartment(ApartmentState.STA)]
public sealed class ConnectionManagementDialogRelayFetchInitiateTests
{
    [Test]
    public async Task SearchAndConnect_RelayMode_GivenBundleFound_EnqueuesHandshakeHelloToRelayHost()
    {
        WpfTestHarness.EnsureApplication();

        var inbox = new Mock<IMainInvitationInbox>(MockBehavior.Loose);
        inbox.Setup(x => x.GetOpenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingInvitationDto>());

        var inboxEvents = new Mock<IMainInvitationInboxEvents>(MockBehavior.Loose);
        inboxEvents.SetupGet(x => x.Changed).Returns(new R3.Subject<R3.Unit>());

        var reverseSignalInvites = new Mock<IMainReverseSignalInviteFactory>(MockBehavior.Loose);
        var grpcSessions = new Mock<IGrpcSessionService>(MockBehavior.Loose);

        var transport = new Mock<IMessageTransportService>(MockBehavior.Loose);
        var secureMessaging = new Mock<ISecureMessagingService>(MockBehavior.Loose);

        var directSessions = new Mock<IDirectSessionRepository>(MockBehavior.Loose);

        var relayHostId = Guid.NewGuid();
        var directSessionId = DirectSessionId.NewId();
        directSessions.Setup(x => x.GetByRemotePeerIdAsync(It.IsAny<Percolator.Network.PeerId>(), It.IsAny<int>()))
            .ReturnsAsync(new DirectSession(new Percolator.Network.PeerId(relayHostId), directSessionId));
        directSessions.Setup(x => x.ListAsync(It.IsAny<int>()))
            .ReturnsAsync(Array.Empty<DirectSession>());

        var active = new ActiveIdentityContext();
        active.SetActiveIdentity(
            new IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = new SelfId(1) },
            new X3dhKeys(
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)));

        var peerIdentities = new Mock<Percolator.Identity.IPeerIdentityRepository>(MockBehavior.Loose);
        var establish = new Mock<IEstablishDirectSessionService>(MockBehavior.Loose);
        var state = new PeerConnectionStateService(Mock.Of<IServiceScopeFactory>(MockBehavior.Loose));
        var ui = new TestUiDispatcher();

        var sessionCrypto = new Mock<ISessionCrypto>(MockBehavior.Loose);
        sessionCrypto.Setup(x => x.VerifySignature(It.IsAny<RatchetIdentityKey>(), It.IsAny<PreKey>(), It.IsAny<Percolator.Cryptography.Signature>()))
            .Returns(true);
        sessionCrypto.Setup(x => x.X3DH_Initiate(It.IsAny<PrivatePreKey>(), It.IsAny<Percolator.Cryptography.PreKeyBundle>()))
            .Returns((SharedSecret.FromBytes(new byte[32]), RatchetEphemeralKey.FromBytes(new byte[64])));

        var preHandshake = new Mock<IPreHandshakeSessionStore>(MockBehavior.Loose);
        var sentInvitations = new Mock<ISentInvitationRepository>(MockBehavior.Loose);

        var clock = new Mock<IClock>(MockBehavior.Loose);
        clock.SetupGet(x => x.UtcNow).Returns(DateTimeOffset.UtcNow);

        byte[] remoteIdentitySpki;
        using (var remoteEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            remoteIdentitySpki = remoteEcdsa.ExportSubjectPublicKeyInfo();
        }

        var targetPkh = SHA256.HashData(remoteIdentitySpki);
        var targetPkhHex = Convert.ToHexString(targetPkh);

        var mediator = new Mock<MediatR.IMediator>(MockBehavior.Loose);
        mediator.Setup(x => x.Send(It.IsAny<GetRelayHostOptionsQuery>(), default))
            .ReturnsAsync(new GetRelayHostOptionsResult(Array.Empty<RelayHostOptionDto>()));
        mediator.Setup(x => x.Send(It.IsAny<ConnectViaNetworkCommand>(), default))
            .ReturnsAsync(new ConnectViaNetworkResult.Success());

        var sut = new ConnectionManagementDialogViewModel(
            inbox.Object,
            inboxEvents.Object,
            active,
            state,
            ui,
            mediator.Object);

        sut.SelectedRouteMode.Value = new RouteModeOption("relay", "Via Relay Host");
        sut.SelectedRelayHost.Value = new RelayHostOption(new Percolator.Network.PeerId(relayHostId), "relay");
        sut.TargetPkhText.Value = targetPkhHex;

        // Act
        sut.SearchAndConnectCommand.Execute(null);

        // Assert - Verify MediatR command was sent with correct parameters (black box testing)
        mediator.Verify(x => x.Send(
            It.Is<ConnectViaNetworkCommand>(c =>
                c.RouteMode == "relay" &&
                c.DirectEndpoint == null &&
                c.TargetPkhText == targetPkhHex &&
                c.RelayHostPeerId.Value == relayHostId),
            default), Times.Once);
    }
}
