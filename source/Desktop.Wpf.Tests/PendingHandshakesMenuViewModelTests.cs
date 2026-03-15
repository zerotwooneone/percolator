using System;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Sessions.Models;
using Desktop.Wpf.Features.Sessions.State;
using Desktop.Wpf.Shared.Windowing;
using FluentAssertions;
using MediatR;
using Moq;
using NUnit.Framework;
using Percolator.Application.Network;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

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

        var store = new Mock<ISecureChannelsStore>(MockBehavior.Loose);
        store.SetupGet(s => s.Channels).Returns(
            new System.Collections.ObjectModel.ReadOnlyObservableCollection<SecureChannelModel>(
                new System.Collections.ObjectModel.ObservableCollection<SecureChannelModel>()));
        store.SetupGet(s => s.PendingInbound).Returns(
            new System.Collections.ObjectModel.ReadOnlyObservableCollection<PendingInvitationModel>(
                new System.Collections.ObjectModel.ObservableCollection<PendingInvitationModel>()));

        var windowManager = new Mock<IWindowManager>(MockBehavior.Loose);

        var sut = new PendingHandshakesMenuViewModel(windowManager.Object, mediator.Object, store.Object);
        var item = new PendingHandshakeItem
        {
            DisplayName = "Alice",
            Initials = "A",
            PendingId = pendingId
        };
        sut.PendingHandshakes.Add(item);

        // ACT
        sut.AcceptHandshakeCommand.Execute(item);

        // ASSERT
        // AsyncRelayCommand is fire-and-forget (async void). Wait for the observable side-effect.
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (item.StatusText != "Accepted" && !cts.IsCancellationRequested)
        {
            await Task.Delay(10, cts.Token);
        }

        item.StatusText.Should().Be("Accepted");
        item.SendPath.Should().Be("Direct");
        item.RequestCorrelationId.Should().NotBeNullOrWhiteSpace();
        item.RequestCorrelationId.Should().Be(requestCorrelationId.Value.ToString());

        mediator.Verify(
            m => m.Send(It.IsAny<ApprovePendingSessionCommand>(), default),
            Times.Once);
    }
}
