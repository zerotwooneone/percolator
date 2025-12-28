using System;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Sessions;
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
        var pendingId = PendingSessionId.NewId();
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        mediator
            .Setup(m => m.Send(
                It.Is<ApprovePendingSessionCommand>(c => c.PendingSessionId == pendingId),
                default))
            .ReturnsAsync(new ApprovePendingSessionResult.Accepted("Direct", new RequestCorrelationId(Guid.NewGuid())));

        var pendingRepo = new Mock<IPendingSessionRepository>(MockBehavior.Strict);

        var sut = new PendingHandshakesMenuViewModel(mediator.Object, pendingRepo.Object);
        var item = new PendingHandshakeItem
        {
            DisplayName = "Alice",
            Initials = "A",
            PendingId = pendingId
        };
        sut.PendingHandshakes.Add(item);

        sut.AcceptHandshakeCommand.Execute(item);

        await Task.Delay(50);

        item.StatusText.Should().Be("Accepted");
        item.SendPath.Should().Be("Direct");
        item.RequestCorrelationId.Should().NotBeNullOrWhiteSpace();

        mediator.VerifyAll();
    }
}
