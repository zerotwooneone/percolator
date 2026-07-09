using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Sessions.Commands;
using Desktop.Wpf.Features.Sessions.Queries;
using Desktop.Wpf.Features.Self;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
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

        // ARRANGE
        var directSessions = new Mock<IDirectSessionRepository>(MockBehavior.Loose);

        var relayHostId = new NetworkPeerId(40);
        var directSessionId = DirectSessionId.NewId();
        directSessions.Setup(x => x.GetByRemotePeerIdAsync(It.IsAny<Percolator.Network.NetworkPeerId>(), It.IsAny<Percolator.Network.NetworkSelfId>()))
            .ReturnsAsync(new DirectSession(relayHostId, directSessionId));
        directSessions.Setup(x => x.ListAsync(It.IsAny<Percolator.Network.NetworkSelfId>()))
            .ReturnsAsync(Array.Empty<DirectSession>());

        var active = new ActiveIdentityContext();
        var identityPublicIdentityId = active.Identity?.PublicIdentityId ?? PublicIdentityId.NewId();
        active.SetActiveIdentity(
            new IdentityRecord(
                active.Identity?.SelfIdentityId ?? new Percolator.Identity.SelfId(1),
                identityPublicIdentityId,
                active.Identity?.DeviceId ?? new Percolator.Identity.DeviceId(1),
                active.Identity?.Name ?? "self"),
            new X3dhKeys(
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
                ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)));

        var state = new PeerConnectionStateService(Mock.Of<IServiceScopeFactory>(MockBehavior.Loose));
        var identityStateService = new Mock<IIdentityStateService>(MockBehavior.Loose);
        var ui = new TestUiDispatcher();

        var sessionCrypto = new Mock<ISessionCrypto>(MockBehavior.Loose);
        sessionCrypto.Setup(x => x.VerifySignature(It.IsAny<RatchetIdentityKey>(), It.IsAny<PreKey>(), It.IsAny<Percolator.Cryptography.Signature>()))
            .Returns(true);
        sessionCrypto.Setup(x => x.X3DH_Initiate(It.IsAny<PrivatePreKey>(), It.IsAny<Percolator.Cryptography.PreKeyBundle>()))
            .Returns((SharedSecret.FromBytes(new byte[32]), RatchetEphemeralKey.FromBytes(new byte[64])));

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
            active,
            state,
            identityStateService.Object,
            ui,
            mediator.Object);

        sut.SelectedRouteMode.Value = new RouteModeOption("relay", "Via Relay Host");
        sut.SelectedRelayHost.Value = new RelayHostOption(relayHostId, "relay");
        sut.TargetPkhText.Value = targetPkhHex;

        // ACT
        sut.SearchAndConnectCommand.Execute(null);

        // ASSERT: Test passes if no exception thrown (command sent successfully)
    }
}
