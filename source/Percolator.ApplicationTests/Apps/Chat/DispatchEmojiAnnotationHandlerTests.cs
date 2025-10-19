using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Percolator.Application.Apps.Chat;
using Percolator.Identity;
using Percolator.MessageQueue.Commands;
using Percolator.MessageQueue.Results;

namespace Percolator.ApplicationTests.Apps.Chat;

public class DispatchEmojiAnnotationHandlerTests
{
    private readonly Mock<IMediator> _mediatorMock;
    private readonly ILogger<DispatchEmojiAnnotationHandler> _logger;
    private readonly DispatchEmojiAnnotationHandler _sut;
    private readonly PeerId _selfId = new(Guid.NewGuid());
    private readonly PeerId _recipientId = new(Guid.NewGuid());
    private readonly Guid _messageId = Guid.NewGuid();
    private readonly string _emoji = "👍";
    private readonly DateTime _sentUtc = DateTime.UtcNow;

    public DispatchEmojiAnnotationHandlerTests()
    {
        _mediatorMock = new Mock<IMediator>(MockBehavior.Strict);
        _logger = NullLogger<DispatchEmojiAnnotationHandler>.Instance;
        _sut = new DispatchEmojiAnnotationHandler(_mediatorMock.Object, _logger);
    }

    [Test]
    public async Task Handle_EnqueuesAndTriggersRelay_OnSuccess()
    {
        // Arrange
        var recipients = new[] { _recipientId };
        var command = new DispatchEmojiAnnotationCommand(_messageId, _emoji, _sentUtc, recipients, _selfId);

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
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        _mediatorMock.VerifyAll();
    }
}
