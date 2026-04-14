using System;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Shared.Mvvm;
using Desktop.Wpf.Shared.Windowing;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Percolator.Application.Network;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Desktop.Wpf.Features.Sessions.Queries;

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

        var state = new PeerConnectionStateService(Mock.Of<IServiceScopeFactory>(MockBehavior.Loose));

        var windowManager = new Mock<IWindowManager>(MockBehavior.Loose);
        var ui = new TestUiDispatcher();

        var sut = new PendingHandshakesMenuViewModel(windowManager.Object, mediator.Object, state, ui);

        state.UpdatePendingInbound(new[]
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
                RelayEndpoint: null)
        });

        var item = sut.PendingHandshakes[0];

        // ACT
        sut.AcceptHandshakeCommand.Execute(item);

        // ASSERT
        // AsyncRelayCommand is fire-and-forget (async void). Wait for the observable side-effect.
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (item.StatusText.Value != "Accepted" && !cts.IsCancellationRequested)
        {
            await Task.Delay(10, cts.Token);
        }

        if (cts.IsCancellationRequested)
        {
            // If timeout occurred, check the actual value for debugging
            Assert.Fail($"Timeout waiting for StatusText to be 'Accepted'. Current value: '{item.StatusText.Value}'");
        }

        item.StatusText.Value.Should().Be("Accepted");
        item.SendPath.Should().Be("Direct");
        item.RequestCorrelationId.Should().NotBeNullOrWhiteSpace();
        item.RequestCorrelationId.Should().Be(requestCorrelationId.Value.ToString());

        mediator.Verify(
            m => m.Send(It.IsAny<ApprovePendingSessionCommand>(), default),
            Times.Once);
    }
}
