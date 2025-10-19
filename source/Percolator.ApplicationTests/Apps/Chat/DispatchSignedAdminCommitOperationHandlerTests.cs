using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Apps.Chat;
using Percolator.Identity;
using Percolator.MessageQueue.Commands;
using Percolator.MessageQueue.Results;

namespace Percolator.ApplicationTests.Apps.Chat;

[TestFixture]
public class DispatchSignedAdminCommitOperationHandlerTests
{
    private Mock<IMediator> _mediatorMock = null!;
    private ILogger<DispatchSignedAdminCommitOperationHandler> _logger = null!;
    private DispatchSignedAdminCommitOperationHandler _sut = null!;

    private PeerId _senderId = new(Guid.NewGuid());
    private PeerId _recipientId = new PeerId(Guid.NewGuid());
    private Guid _groupId = Guid.NewGuid();
    private Guid _opId = Guid.NewGuid();
    private DateTime _sentUtc = DateTime.UtcNow;

    [SetUp]
    public void SetUp()
    {
        _mediatorMock = new Mock<IMediator>(MockBehavior.Strict);
        _logger = NullLogger<DispatchSignedAdminCommitOperationHandler>.Instance;
        _sut = new DispatchSignedAdminCommitOperationHandler(_mediatorMock.Object, _logger);
    }

    [Test]
    public async Task Handle_EnqueuesAndTriggersRelay_OnSuccess()
    {
        // Arrange
        var recipients = new[] { _recipientId };
        var cmd = new DispatchSignedAdminCommitOperationCommand(
            _groupId,
            _opId,
            7,
            _sentUtc,
            recipients,
            _senderId,
            new byte[] { 9, 9, 9 },
            null);

        _mediatorMock
            .Setup(m => m.Send(
                It.Is<EnqueueOpaqueMessageCommand>(c =>
                    c.RecipientPublicKeyHash.SequenceEqual(_recipientId.Value.ToByteArray()) &&
                    c.MessageBlob != null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnqueueOpaqueMessageResult(true, null));

        _mediatorMock
            .Setup(m => m.Send(
                It.Is<Percolator.Application.Network.TryRelayNextForPeerCommand>(c => c.RecipientPeerId.Equals(_recipientId)),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _sut.Handle(cmd, CancellationToken.None);

        // Assert
        _mediatorMock.VerifyAll();
    }
}
