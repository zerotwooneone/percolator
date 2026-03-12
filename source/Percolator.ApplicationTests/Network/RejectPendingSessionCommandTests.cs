using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Network;
using Percolator.ApplicationTests.Services;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public sealed class RejectPendingSessionCommandTests
{
    [Test]
    public async Task RejectPendingSession_WhenNotFound_ReturnsNotFound()
    {
        var pendingRepo = new Mock<IPendingSessionRepository>(MockBehavior.Strict);
        var id = PendingSessionId.NewId();

        pendingRepo.Setup(r => r.GetAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PendingSession?)null);

        var sut = new RejectPendingSessionHandler(pendingRepo.Object);

        var result = await sut.Handle(new RejectPendingSessionCommand(id), CancellationToken.None);

        result.Should().BeOfType<RejectPendingSessionResult.NotFound>();
    }

    [Test]
    public async Task RejectPendingSession_WhenAwaitingApproval_UpdatesAndReturnsRejected()
    {
        var pendingRepo = new Mock<IPendingSessionRepository>(MockBehavior.Strict);
        var id = PendingSessionId.NewId();

        var pending = PendingSession.FromInvitationWithMetadata(
            id,
            new Percolator.Cryptography.Primitives.PeerId(Guid.NewGuid()),
            new ProtocolVersion(1),
            new HandshakeInvitation(new byte[] { 1, 2, 3 }),
            requestCorrelationId: new RequestCorrelationId(Guid.NewGuid()),
            isRelayed: false,
            relayHostPeerId: null,
            inviterIdentityKey: null,
            callbackEndpointHost: null,
            callbackEndpointPort: null,
            new TestClock());

        pendingRepo.Setup(r => r.GetAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pending);
        pendingRepo.Setup(r => r.UpdateAsync(pending, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new RejectPendingSessionHandler(pendingRepo.Object);

        var result = await sut.Handle(new RejectPendingSessionCommand(id), CancellationToken.None);

        result.Should().BeOfType<RejectPendingSessionResult.Rejected>();
    }
}
