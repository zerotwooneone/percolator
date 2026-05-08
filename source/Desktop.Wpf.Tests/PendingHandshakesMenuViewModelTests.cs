using System;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Shared.Windowing;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Identity;

namespace Desktop.Wpf.Tests;

[TestFixture]
public sealed class PendingHandshakesMenuViewModelTests
{
    [Test]
    public async Task AcceptHandshake_SendsApproveCommand_AndUpdatesItemFromTypedResult()
    {
        // ARRANGE
        var pendingId = PendingSessionId.NewId();
        var requestCorrelationId = new RequestCorrelationId(Guid.NewGuid());
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        mediator
            .Setup(m => m.Send(
                It.Is<ApprovePendingSessionCommand>(c => c.PendingSessionId == pendingId),
                default))
            .ReturnsAsync(new ApprovePendingSessionResult.Accepted("Direct", requestCorrelationId));

        var scope = new Mock<IServiceScope>();
        var provider = new Mock<IServiceProvider>();
        var sidebarQueries = new Mock<Percolator.Application.Sessions.IPeerConnectionSidebarQueries>();
        sidebarQueries.Setup(q => q.LoadSidebarConnectionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Percolator.Application.Sessions.SidebarPeerConnectionDto>());
        provider.Setup(p => p.GetService(typeof(Percolator.Application.Sessions.IPeerConnectionSidebarQueries)))
            .Returns(sidebarQueries.Object);
        
        var inboundQueries = new Mock<Percolator.Application.Sessions.IPeerConnectionQueries>();
        inboundQueries.Setup(q => q.LoadPendingInboundAsync(It.IsAny<SelfId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Percolator.Application.Sessions.PendingInboundSnapshot>());
        provider.Setup(p => p.GetService(typeof(Percolator.Application.Sessions.IPeerConnectionQueries)))
            .Returns(inboundQueries.Object);
        
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);
        
        var state = new PeerConnectionStateService(scopeFactory.Object);
        var selfId = new SelfId(1);
        state.InitializeAsync(selfId, CancellationToken.None).GetAwaiter().GetResult();

        var windowManager = new Mock<IWindowManager>(MockBehavior.Loose);
        var ui = new TestUiDispatcher();
        var activeIdentity = new ActiveIdentityContext();
        var identityRecord = new Percolator.Identity.Model.IdentityRecord(Guid.NewGuid(), "self") { SelfIdentityId = selfId };
        activeIdentity.SetActiveIdentity(identityRecord, new X3dhKeys(
            System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256),
            System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256)));

        var sut = new PendingHandshakesMenuViewModel(windowManager.Object, mediator.Object, state, activeIdentity, ui);

        state.UpdatePendingInbound(selfId, new[]
        {
            new PendingInboundSnapshot(
                PendingSessionId: pendingId.Value,
                RequestCorrelationId: requestCorrelationId.Value,
                PeerId: Guid.NewGuid(),
                PeerName: "Alice",
                InviterFingerprintHex: null,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                ExpiresAtUtc: null,
                IsRelayed: false,
                RelayPeerId: null,
                RelayPeerName: null,
                RelayEndpoint: null,
                SelfIdentityId: selfId)
        });

        var item = sut.PendingHandshakes[0];

        // ACT
        sut.AcceptHandshakeCommand.Execute(item);

        // ASSERT: Item properties updated after command execution
        item.StatusText.Value.Should().Be("Accepted");
        item.SendPath.Should().Be("Direct");
        item.RequestCorrelationId.Should().NotBeNullOrWhiteSpace();
        item.RequestCorrelationId.Should().Be(requestCorrelationId.Value.ToString());
    }
}
